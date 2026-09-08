using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises initial migration and restart decisions against the actual generation-pinned JSON repository.</summary>
public sealed class SettingsAuthorityBootstrapperTests
{
    [Fact]
    public async Task ExistingAuthority_IsReusedWithoutReadingChangedLegacySettings()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        Source source = new() { Port = 7890 };
        SettingsAuthorityBootstrapper bootstrap = Create(generation, source);
        Assert.Equal(0, source.Reads);
        SettingsPersistenceResult first = await bootstrap.OpenAsync(Guid.NewGuid(), CancellationToken.None);
        Source unavailableLegacy = new() { Failure = new IOException("legacy unavailable") };

        SettingsPersistenceResult reopened = await Create(generation, unavailableLegacy).OpenAsync(Guid.NewGuid(), CancellationToken.None);

        Assert.True(first.IsSucceeded, first.Diagnostic?.Code);
        Assert.True(reopened.IsSucceeded, reopened.Diagnostic?.Code);
        Assert.Equal(1, source.Reads);
        Assert.Equal(0, unavailableLegacy.Reads);
        Assert.Equal(Hash(first), Hash(reopened));
        Assert.Equal(7890, reopened.Envelope!.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
    }

    [Fact]
    public async Task CorruptionAcrossRepeatedStartup_NeverAuthorizesLegacyReinitialization()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        JsonSettingsRepository repository = new(generation, SettingsRegistry.Default);
        repository.EnsureLayout();
        await File.WriteAllTextAsync(repository.PrimaryPath, "{corrupt");
        Source source = new();
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            SettingsPersistenceResult result = await Create(generation, source).OpenAsync(Guid.NewGuid(), CancellationToken.None);
            Assert.Equal(SettingsPersistenceStatus.Corrupt, result.Status);
            Assert.Null(result.Envelope);
        }

        Assert.Equal(0, source.Reads);
        Assert.False(File.Exists(repository.PrimaryPath));
    }

    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task InterruptedInitialPublication_ReopeningResolvesTheActualDurableDecision(SettingsPersistenceFaultPoint cut)
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        Source source = new() { Port = 7890 };
        Guid firstId = Guid.NewGuid();
        SettingsPersistenceResult interrupted = await Create(generation, source, new Fault(cut))
            .OpenAsync(firstId, CancellationToken.None);
        Assert.Equal(SettingsPersistenceStatus.Unavailable, interrupted.Status);
        Source nextSource = new() { Port = 10001 };
        Guid nextId = Guid.NewGuid();

        SettingsPersistenceResult restarted = await Create(generation, nextSource).OpenAsync(nextId, CancellationToken.None);

        Assert.True(restarted.IsSucceeded, restarted.Diagnostic?.Code);
        bool published = cut == SettingsPersistenceFaultPoint.AfterEnvelopePromotion;
        Assert.Equal(published ? 0 : 1, nextSource.Reads);
        Assert.Equal(published ? 7890 : 10001, restarted.Envelope!.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.Equal(published ? firstId : nextId, Assert.Single(restarted.Envelope.MigrationHistory).MigrationId);
    }

    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task CallerCancellation_RespectsTheRepositoryPublicationBoundary(SettingsPersistenceFaultPoint cut)
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        using CancellationTokenSource cancellation = new();
        Source source = new();
        SettingsAuthorityBootstrapper bootstrap = Create(generation, source, new Fault(cut, cancellation));
        if (cut == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => bootstrap.OpenAsync(Guid.NewGuid(), cancellation.Token));
        }
        else
        {
            Assert.True((await bootstrap.OpenAsync(Guid.NewGuid(), cancellation.Token)).IsSucceeded);
        }

        Source next = new();
        Assert.True((await Create(generation, next).OpenAsync(Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        Assert.Equal(cut == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion ? 1 : 0, next.Reads);
    }

    [Fact]
    public async Task TwoInitializers_ReturnTheSameWinningAuthorityWithoutOverwritingIt()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        TaskCompletionSource bothRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int arrivals = 0;
        async Task WaitForBoth(CancellationToken token)
        {
            if (Interlocked.Increment(ref arrivals) == 2) { bothRead.TrySetResult(); }
            await bothRead.Task.WaitAsync(token);
        }

        Source first = new() { Port = 7890, BeforeRead = WaitForBoth };
        Source second = new() { Port = 10001, BeforeRead = WaitForBoth };
        SettingsPersistenceResult[] results = await Task.WhenAll(
            Create(generation, first).OpenAsync(Guid.NewGuid(), deadline.Token),
            Create(generation, second).OpenAsync(Guid.NewGuid(), deadline.Token));

        Assert.All(results, value => Assert.True(value.IsSucceeded, value.Diagnostic?.Code));
        Assert.Equal(Hash(results[0]), Hash(results[1]));
        Assert.Equal(1, results[0].Envelope!.EnvelopeRevision);
        Assert.Equal(1, first.Reads);
        Assert.Equal(1, second.Reads);
    }

    [Fact]
    public async Task FailedLegacyObservation_DoesNotCreateAnAuthority()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        IOException failure = new("read failed");
        Source source = new() { Failure = failure };

        Assert.Same(failure, await Record.ExceptionAsync(() => Create(generation, source).OpenAsync(Guid.NewGuid(), CancellationToken.None)));
        SettingsPersistenceResult result = await new JsonSettingsRepository(generation, SettingsRegistry.Default).OpenAsync(CancellationToken.None);
        Assert.True(result.IsSucceeded);
        Assert.Null(result.Envelope);
    }

    private static SettingsAuthorityBootstrapper Create(DataGenerationDescriptor generation, Source source, ISettingsPersistenceFaultInjector? fault = null) =>
        new(new JsonSettingsRepository(generation, SettingsRegistry.Default, fault), source, new SettingsMigrationPlanner(SettingsRegistry.Default));

    private static string Hash(SettingsPersistenceResult result) => SettingsEnvelopeCodec.Encode(result.Envelope!, SettingsRegistry.Default).ContentHash;

    private sealed class Source : ILegacySettingsSource
    {
        public int Port { get; init; } = 7890;
        public int Reads { get; private set; }
        public Exception? Failure { get; init; }
        public Func<CancellationToken, Task>? BeforeRead { get; init; }
        public async Task<LegacySettingsSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            ++Reads;
            if (Failure is not null) { throw Failure; }
            if (BeforeRead is not null) { await BeforeRead(cancellationToken); }
            cancellationToken.ThrowIfCancellationRequested();
            return new(SettingsRegistry.Default, new Dictionary<string, object?> { ["MixedPort"] = Port });
        }
    }

    private sealed class Fault(SettingsPersistenceFaultPoint selected, CancellationTokenSource? cancellation = null) : ISettingsPersistenceFaultInjector
    {
        public Task InjectAsync(SettingsPersistenceFaultPoint point, CancellationToken cancellationToken)
        {
            if (point == selected)
            {
                if (cancellation is null) { throw new IOException("publication interrupted"); }
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }
    }
}
