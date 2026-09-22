using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Checks that actual settings repositories, actor lifetime, and durable generation manifests move together.</summary>
public sealed class SettingsGenerationLifetimeTests
{
    [Fact]
    public async Task Transition_WaitsForOwnedSettingsWorkAndSubsequentCommandsResolveOnlyTheNewSession()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        MutationAdmissionBarrier admission = new();
        Lifetime oldLifetime = await Lifetime.CreateAsync(baseline.Descriptor, admission);
        await using DataGenerationManager manager = new();
        manager.Initialize(baseline, new(baseline.Descriptor, oldLifetime));
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<SettingsAuthorityResult> ChangeOldAsync()
        {
            await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(deadline.Token);
            return await manager.ExecuteAsync<SettingsAuthoritySession, SettingsAuthorityResult>(async (session, generation, token) =>
            {
                Assert.True(session.Generation.IsSameGeneration(generation));
                entered.TrySetResult();
                await release.Task.WaitAsync(deadline.Token);
                return await session.ChangeAdmittedAsync([Change("7890")], Guid.NewGuid(), lease, token);
            }, deadline.Token);
        }

        Task<SettingsAuthorityResult> changing = ChangeOldAsync();
        Task<DataGenerationTransition>? draining = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            draining = manager.BeginDrainAsync(baseline.ContentHash, deadline.Token).AsTask();
            Assert.False(draining.IsCompleted);
            Assert.Equal(0, oldLifetime.Disposals);
            Assert.Throws<DataGenerationManagerException>(() => manager.ReadSnapshot<SettingsAuthoritySession, SettingsEnvelope>(
                (session, _) => session.Snapshot));
        }
        finally
        {
            release.TrySetResult();
            await changing;
        }

        Assert.True((await changing).IsSucceeded);
        DataGenerationTransition transition = await draining!;
        Assert.Equal("7890", oldLifetime.Session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.CanonicalText);
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        Lifetime nextLifetime = await Lifetime.CreateAsync(candidate, admission);
        transition.Stage(new(candidate, nextLifetime));
        await transition.PromoteManifestAsync(directory.Store, deadline.Token);
        transition.SwapToPromoted();
        await transition.CommitAsync();
        Assert.Equal(1, oldLifetime.Disposals);
        Assert.Throws<ObjectDisposedException>(() => oldLifetime.Session.Snapshot);

        await using MutationAdmissionLease currentLease = await admission.AcquireOrdinaryAsync(deadline.Token);
        SettingsAuthorityResult current = await manager.ExecuteAsync<SettingsAuthoritySession, SettingsAuthorityResult>(
            (session, generation, token) =>
            {
                Assert.True(candidate.IsSameGeneration(generation));
                Assert.Same(nextLifetime.Session, session);
                return session.ChangeAdmittedAsync([Change("10001")], Guid.NewGuid(), currentLease, token);
            }, deadline.Token);
        Assert.True(current.IsSucceeded);
        Assert.Equal("10001", manager.ReadSnapshot<SettingsAuthoritySession, string>(
            (session, _) => session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.CanonicalText));
        SettingsEnvelope oldBytes = (await new JsonSettingsRepository(baseline.Descriptor, SettingsRegistry.Default)
            .OpenAsync(deadline.Token)).Envelope!;
        Assert.Equal("7890", oldBytes.Desired[SettingsRegistry.Keys.MixedPort].Value.CanonicalText);
        Assert.Equal(candidate.GenerationId, (await directory.Store.LoadCurrentAsync(deadline.Token))!.Descriptor.GenerationId);
    }

    [Fact]
    public async Task Rollback_RestoresTheOriginalSessionAndDisposesOnlyTheCandidate()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        MutationAdmissionBarrier admission = new();
        Lifetime original = await Lifetime.CreateAsync(baseline.Descriptor, admission);
        await using DataGenerationManager manager = new();
        manager.Initialize(baseline, new(baseline.Descriptor, original));
        DataGenerationTransition transition = await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        Lifetime rejected = await Lifetime.CreateAsync(candidate, admission);
        transition.Stage(new(candidate, rejected));
        await transition.PromoteManifestAsync(directory.Store, CancellationToken.None);
        transition.SwapToPromoted();
        await transition.RestoreBaselineAsync(directory.Store, CancellationToken.None);

        Assert.Equal(0, original.Disposals);
        Assert.Equal(1, rejected.Disposals);
        Assert.Throws<ObjectDisposedException>(() => rejected.Session.Snapshot);
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(CancellationToken.None);
        SettingsAuthorityResult result = await manager.ExecuteAsync<SettingsAuthoritySession, SettingsAuthorityResult>(
            (session, generation, token) =>
            {
                Assert.Same(original.Session, session);
                Assert.True(baseline.Descriptor.IsSameGeneration(generation));
                return session.ChangeAdmittedAsync([Change("7890")], Guid.NewGuid(), lease, token);
            }, CancellationToken.None);
        Assert.True(result.IsSucceeded);
        Assert.Equal(baseline.Descriptor.GenerationId, manager.CurrentManifest.Descriptor.GenerationId);
        Assert.Equal(manager.CurrentManifest.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedResolutionOrOperation_ReleasesItsPinSoDrainCanComplete(bool operationThrows)
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        MutationAdmissionBarrier admission = new();
        Lifetime lifetime = await Lifetime.CreateAsync(baseline.Descriptor, admission);
        await using DataGenerationManager manager = new();
        manager.Initialize(baseline, new(baseline.Descriptor, lifetime));
        if (operationThrows)
        {
            IOException failure = new("owned operation failure");
            Assert.Same(failure, await Record.ExceptionAsync(() => manager.ExecuteAsync<SettingsAuthoritySession, int>(
                (_, _, _) => Task.FromException<int>(failure), CancellationToken.None)));
        }
        else
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => manager.ExecuteAsync<ISettingsApplicationParticipant, int>(
                (_, _, _) => Task.FromResult(1), CancellationToken.None));
        }

        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
        DataGenerationTransition transition = await manager.BeginDrainAsync(baseline.ContentHash, deadline.Token);
        await transition.AbortAsync();
        Assert.Equal(baseline.ContentHash, manager.CurrentManifest.ContentHash);
    }

    private static SettingValueChange Change(string port) =>
        new(SettingsRegistry.Keys.MixedPort, SettingsEnvelopeTestData.Value("MixedPort", port));

    private sealed class Lifetime(SettingsAuthoritySession session) : IServiceProvider, IAsyncDisposable
    {
        public SettingsAuthoritySession Session { get; } = session;
        public int Disposals { get; private set; }
        public object? GetService(Type serviceType) => serviceType == typeof(SettingsAuthoritySession) ? Session : null;

        public static async Task<Lifetime> CreateAsync(DataGenerationDescriptor generation, MutationAdmissionBarrier admission)
        {
            JsonSettingsRepository repository = new(generation, SettingsRegistry.Default);
            Assert.True((await repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
            return new(new(repository, SettingsRegistry.Default, admission));
        }

        public async ValueTask DisposeAsync()
        {
            ++Disposals;
            await Session.DisposeAsync();
        }
    }
}
