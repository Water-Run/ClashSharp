using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Integration;

public sealed class DataGenerationBootstrapperTests
{
    [Fact]
    public async Task FirstStartup_PublishesOnlyAfterCompleteScopeOpening_AndRestartNeverReadsLegacy()
    {
        await using DataGenerationTestDirectory directory = new();
        Fixture factory = new(directory);
        await using DataGenerationManager manager = new();
        DataGenerationBootstrapper bootstrap = new(directory.Store, factory, manager, factory.Admission);
        Assert.False(Directory.Exists(directory.RootPath));
        Assert.Equal(0, factory.Source.Reads);
        await using MutationAdmissionLease lease = await factory.ExclusiveAsync();
        factory.BeforeOpen = descriptor =>
        {
            Assert.False(File.Exists(directory.Policy.CurrentManifestPath));
            Assert.Throws<DataGenerationManagerException>(() => manager.CurrentManifest);
            Assert.True(File.Exists(new JsonSettingsRepository(descriptor, SettingsRegistry.Default).PrimaryPath));
            return Task.CompletedTask;
        };

        DataGenerationManifestSnapshot first = await bootstrap.InitializeAdmittedAsync(lease, CancellationToken.None);
        Assert.True(first.Descriptor.IsSameGeneration(manager.CurrentManifest.Descriptor));
        Assert.Equal(1, factory.Source.Reads);
        Assert.Equal(7890, manager.ReadSnapshot<SettingsAuthoritySession, int>((session, _) =>
            session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>()));
        Assert.Same(first, await bootstrap.InitializeAdmittedAsync(lease, CancellationToken.None));
        Assert.Equal(1, factory.Opens);
        await manager.DisposeAsync();
        Assert.Equal(1, factory.Lifetimes.Single().Disposals);

        await using DataGenerationManager restarted = new();
        Fixture nextFactory = new(directory);
        nextFactory.Source.Failure = new IOException("Legacy is unavailable after cutover.");
        await using MutationAdmissionLease nextLease = await nextFactory.ExclusiveAsync();
        DataGenerationManifestSnapshot next = await new DataGenerationBootstrapper(directory.Store, nextFactory, restarted, nextFactory.Admission)
            .InitializeAdmittedAsync(nextLease, CancellationToken.None);
        Assert.Equal(first.ContentHash, next.ContentHash);
        Assert.Equal(0, nextFactory.Creates);
        Assert.Equal(0, nextFactory.Source.Reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostPublicationReply_OpensActualWinnerEvenWhenCallerCanceled(bool cancel)
    {
        await using DataGenerationTestDirectory directory = new();
        Fixture factory = new(directory);
        using CancellationTokenSource cancellation = new();
        FileDataGenerationStore store = new(directory.RootPath, new Fault(DataGenerationFaultPoint.AfterManifestPromotion,
            () => { if (cancel) { cancellation.Cancel(); } }));
        await using DataGenerationManager manager = new();
        await using MutationAdmissionLease lease = await factory.ExclusiveAsync();

        DataGenerationManifestSnapshot result = await new DataGenerationBootstrapper(store, factory, manager, factory.Admission)
            .InitializeAdmittedAsync(lease, cancellation.Token);

        Assert.Equal(result.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.Equal(1, factory.Creates);
        Assert.Equal(1, factory.Opens);
        Assert.Equal(0, factory.Lifetimes.Single().Disposals);
        Assert.Equal(DataGenerationScopeState.Active, factory.Scopes.Single().State);
    }

    [Fact]
    public async Task FailureBeforePublication_RetiresCandidateWithoutReplacingLegacy_AndAllowsFreshRetry()
    {
        await using DataGenerationTestDirectory directory = new();
        Fixture factory = new(directory);
        FileDataGenerationStore store = new(directory.RootPath, new Fault(DataGenerationFaultPoint.BeforeManifestPromotion));
        await using DataGenerationManager manager = new();
        await using MutationAdmissionLease lease = await factory.ExclusiveAsync();
        await Assert.ThrowsAsync<DataGenerationStoreException>(() => new DataGenerationBootstrapper(store, factory, manager, factory.Admission)
            .InitializeAdmittedAsync(lease, CancellationToken.None));
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.Equal(1, factory.Lifetimes.Single().Disposals);
        Assert.Throws<DataGenerationManagerException>(() => manager.CurrentManifest);
        DataGenerationDescriptor rejected = factory.Scopes.Single().Descriptor;
        Assert.True(File.Exists(new JsonSettingsRepository(rejected, SettingsRegistry.Default).PrimaryPath));

        DataGenerationManifestSnapshot accepted = await new DataGenerationBootstrapper(directory.Store, factory, manager, factory.Admission)
            .InitializeAdmittedAsync(lease, CancellationToken.None);
        Assert.NotEqual(rejected.GenerationId, accepted.Descriptor.GenerationId);
        Assert.Equal(2, factory.Source.Reads);
    }

    [Fact]
    public async Task CompetingFirstPublication_ClosesLosingContainerAndOpensVerifiedWinner()
    {
        await using DataGenerationTestDirectory directory = new();
        Fixture factory = new(directory);
        Fixture competitor = new(directory);
        await using MutationAdmissionLease competingLease = await competitor.ExclusiveAsync();
        DataGenerationScope competingScope = await competitor.CreateInitialAsync(competingLease, CancellationToken.None);
        await competingScope.DisposeAsync();
        DataGenerationManifestSnapshot? winning = null;
        factory.BeforeOpen = async _ =>
        {
            factory.BeforeOpen = null;
            winning = await directory.Store.PromoteAsync(competingScope.Descriptor, null, CancellationToken.None);
        };
        await using DataGenerationManager manager = new();
        await using MutationAdmissionLease lease = await factory.ExclusiveAsync();

        DataGenerationManifestSnapshot result = await new DataGenerationBootstrapper(directory.Store, factory, manager, factory.Admission)
            .InitializeAdmittedAsync(lease, CancellationToken.None);

        Assert.Equal(winning!.ContentHash, result.ContentHash);
        Assert.Equal(2, factory.Opens);
        Assert.Equal(1, factory.Lifetimes[0].Disposals);
        Assert.Equal(0, factory.Lifetimes[1].Disposals);
        Assert.Equal(DataGenerationScopeState.Disposed, factory.Scopes[0].State);
        Assert.True(competingScope.Descriptor.IsSameGeneration(factory.Scopes[1].Descriptor));
    }

    [Fact]
    public async Task ExistingCorruptManifest_NeverAuthorizesLegacyMigration()
    {
        await using DataGenerationTestDirectory directory = new();
        directory.Policy.EnsureLayout();
        await File.WriteAllTextAsync(directory.Policy.CurrentManifestPath, "{corrupt");
        Fixture factory = new(directory);
        await using DataGenerationManager manager = new();
        await using MutationAdmissionLease lease = await factory.ExclusiveAsync();
        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(() =>
            new DataGenerationBootstrapper(directory.Store, factory, manager, factory.Admission).InitializeAdmittedAsync(lease, CancellationToken.None));
        Assert.Equal(DataGenerationStoreError.Corrupt, exception.Error);
        Assert.Equal(0, factory.Creates);
        Assert.Equal(0, factory.Source.Reads);
    }

    [Fact]
    public async Task ExistingMissingSettings_FailsWithoutFallingBackToLegacy()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot existing = await directory.PromoteFirstAsync();
        Fixture factory = new(directory);
        await using DataGenerationManager manager = new();
        await using MutationAdmissionLease lease = await factory.ExclusiveAsync();
        await Assert.ThrowsAsync<InvalidDataException>(() => new DataGenerationBootstrapper(directory.Store, factory, manager, factory.Admission)
            .InitializeAdmittedAsync(lease, CancellationToken.None));
        Assert.Equal(0, factory.Source.Reads);
        Assert.Equal(0, factory.Creates);
        Assert.Equal(existing.ContentHash, (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
        Assert.Throws<DataGenerationManagerException>(() => manager.CurrentManifest);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryOrForeignOwnership_IsRejectedBeforeStorageAccess(bool foreign)
    {
        await using DataGenerationTestDirectory directory = new();
        Fixture factory = new(directory);
        await using DataGenerationManager manager = new();
        await using MutationAdmissionLease lease = foreign
            ? await new MutationAdmissionBarrier().CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None)
            : await factory.Admission.AcquireOrdinaryAsync(CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new DataGenerationBootstrapper(directory.Store, factory, manager, factory.Admission)
            .InitializeAdmittedAsync(lease, CancellationToken.None));
        Assert.False(Directory.Exists(directory.RootPath));
        Assert.Equal(0, factory.Creates);
    }

    [Fact]
    public async Task CancelDuringPreparation_DrainsFactoryTaskWithoutPublishingOrLeakingItsScope()
    {
        await using DataGenerationTestDirectory directory = new();
        Fixture factory = new(directory);
        using CancellationTokenSource cancellation = new();
        factory.BeforeOpen = _ => { cancellation.Cancel(); return Task.CompletedTask; };
        await using DataGenerationManager manager = new();
        await using MutationAdmissionLease lease = await factory.ExclusiveAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new DataGenerationBootstrapper(directory.Store, factory, manager, factory.Admission)
            .InitializeAdmittedAsync(lease, cancellation.Token));
        Assert.Null(await directory.Store.LoadCurrentAsync(CancellationToken.None));
        Assert.All(factory.Lifetimes, lifetime => Assert.Equal(1, lifetime.Disposals));
        Assert.Throws<DataGenerationManagerException>(() => manager.CurrentManifest);
    }

    private sealed class Fixture(DataGenerationTestDirectory directory) : IDataGenerationBootstrapFactory
    {
        public MutationAdmissionBarrier Admission { get; } = new();
        public Source Source { get; } = new();
        public List<Lifetime> Lifetimes { get; } = [];
        public List<DataGenerationScope> Scopes { get; } = [];
        public Func<DataGenerationDescriptor, Task>? BeforeOpen { get; set; }
        public int Creates { get; private set; }
        public int Opens { get; private set; }

        public ValueTask<MutationAdmissionLease> ExclusiveAsync() =>
            Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);

        public async Task<DataGenerationScope> CreateInitialAsync(MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            ++Creates;
            LegacyDataGenerationPreparer preparation = new(directory.RootPath, Admission, Source, SettingsRegistry.Default);
            DataGenerationDescriptor candidate = await preparation.PrepareAdmittedAsync(admissionLease, cancellationToken);
            return await OpenAsync(candidate, admissionLease, cancellationToken);
        }

        public async Task<DataGenerationScope> OpenAsync(
            DataGenerationDescriptor descriptor, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            ++Opens;
            if (BeforeOpen is not null) { await BeforeOpen(descriptor); }
            SettingsAuthoritySession session = new(new JsonSettingsRepository(descriptor, SettingsRegistry.Default), SettingsRegistry.Default, Admission);
            Lifetime lifetime = new(session);
            Lifetimes.Add(lifetime);
            try
            {
                SettingsAuthorityResult opened = await session.OpenAdmittedAsync(admissionLease, cancellationToken);
                if (!opened.IsSucceeded || opened.Envelope is null) { throw new InvalidDataException("The published settings repository is not usable."); }
                DataGenerationScope scope = new(descriptor, lifetime);
                Scopes.Add(scope);
                return scope;
            }
            catch { await lifetime.DisposeAsync(); throw; }
        }
    }

    private sealed class Lifetime(SettingsAuthoritySession session) : IServiceProvider, IAsyncDisposable
    {
        public int Disposals { get; private set; }
        public object? GetService(Type serviceType) => serviceType == typeof(SettingsAuthoritySession) ? session : null;
        public async ValueTask DisposeAsync() { ++Disposals; await session.DisposeAsync(); }
    }

    private sealed class Source : ILegacySettingsSource
    {
        public int Reads { get; private set; }
        public Exception? Failure { get; set; }
        public Task<LegacySettingsSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken)
        {
            ++Reads;
            cancellationToken.ThrowIfCancellationRequested();
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(new LegacySettingsSnapshot(SettingsRegistry.Default, new Dictionary<string, object?> { ["MixedPort"] = 7890 }));
        }
    }

    private sealed class Fault(DataGenerationFaultPoint point, Action? action = null) : IDataGenerationFaultInjector
    {
        public Task InjectAsync(DataGenerationFaultPoint actual, CancellationToken cancellationToken)
        {
            if (actual == point) { action?.Invoke(); throw new IOException("Injected manifest publication interruption."); }
            return Task.CompletedTask;
        }
    }
}
