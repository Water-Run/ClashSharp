using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies transition classification at durable store fault boundaries.</summary>
public sealed class DataGenerationTransitionStoreIntegrationTests
{
    /// <summary>Verifies a pre-promotion failure aborts only after re-reading the exact baseline.</summary>
    [Fact]
    public async Task PromoteManifestAsync_PreCommitFailure_AbortsCandidateAndReopensBaseline()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        transition.Stage(new DataGenerationScope(
            directory.CreateGeneration(2),
            candidateLifetime));
        BlockingDataGenerationFaultInjector injector = new(
            DataGenerationFaultPoint.BeforeManifestPromotion,
            throwAfterRelease: true);
        FileDataGenerationStore failingStore = new(directory.RootPath, injector);

        Task<DataGenerationManifestSnapshot> promotion =
            transition.PromoteManifestAsync(failingStore, CancellationToken.None);
        await injector.Entered;
        injector.Release();
        DataGenerationStoreException failure =
            await Assert.ThrowsAsync<DataGenerationStoreException>(() => promotion);

        Assert.Equal(DataGenerationStoreError.Unavailable, failure.Error);
        Assert.Equal(1, candidateLifetime.DisposeCount);
        DataGenerationManifestSnapshot durable =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(baseline.ContentHash, durable.ContentHash);
        await using DataGenerationLease lease =
            await manager.AcquireAsync(CancellationToken.None);
        Assert.Equal(baseline.Descriptor.GenerationId, lease.Descriptor.GenerationId);
        await lease.DisposeAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies post-promotion failure is classified as committed until explicit restoration.</summary>
    [Fact]
    public async Task PromoteManifestAsync_PostCommitFailure_PreservesCandidateForRestoration()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        transition.Stage(new DataGenerationScope(candidate, candidateLifetime));
        BlockingDataGenerationFaultInjector injector = new(
            DataGenerationFaultPoint.AfterManifestPromotion,
            throwAfterRelease: true);
        FileDataGenerationStore failingStore = new(directory.RootPath, injector);

        Task<DataGenerationManifestSnapshot> promotion =
            transition.PromoteManifestAsync(failingStore, CancellationToken.None);
        await injector.Entered;
        injector.Release();
        DataGenerationManagerException failure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(() => promotion);

        Assert.Equal(DataGenerationManagerError.ManifestPromotionCommitted, failure.Error);
        Assert.True(transition.IsManifestPromoted);
        Assert.Equal(0, candidateLifetime.DisposeCount);
        Assert.Equal(baseline.ContentHash, manager.CurrentManifest.ContentHash);
        DataGenerationManifestSnapshot durableCandidate =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(candidate.GenerationId, durableCandidate.Descriptor.GenerationId);

        DataGenerationManifestSnapshot restored = await transition.RestoreBaselineAsync(
            directory.Store,
            CancellationToken.None);
        Assert.Equal(baseline.Descriptor.GenerationId, restored.Descriptor.GenerationId);
        Assert.Equal(restored.ContentHash, manager.CurrentManifest.ContentHash);
        Assert.Equal(1, candidateLifetime.DisposeCount);
        await manager.DisposeAsync();
    }

    /// <summary>Verifies post-restoration failure still aligns memory with the restored baseline.</summary>
    [Fact]
    public async Task RestoreBaselineAsync_PostCommitFailure_ClassifiesAndCompletesRollback()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        CountingAsyncDisposable baselineLifetime = new();
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline, baselineLifetime);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        transition.Stage(new DataGenerationScope(
            directory.CreateGeneration(2),
            candidateLifetime));
        await transition.PromoteManifestAsync(directory.Store, CancellationToken.None);
        transition.SwapToPromoted();
        BlockingDataGenerationFaultInjector injector = new(
            DataGenerationFaultPoint.AfterManifestPromotion,
            throwAfterRelease: true);
        FileDataGenerationStore failingStore = new(directory.RootPath, injector);

        Task<DataGenerationManifestSnapshot> restoration =
            transition.RestoreBaselineAsync(failingStore, CancellationToken.None);
        await injector.Entered;
        injector.Release();
        DataGenerationManagerException failure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(() => restoration);

        Assert.Equal(DataGenerationManagerError.ManifestRestorationCommitted, failure.Error);
        DataGenerationManifestSnapshot durable =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(baseline.Descriptor.GenerationId, durable.Descriptor.GenerationId);
        Assert.Equal(durable.ContentHash, manager.CurrentManifest.ContentHash);
        Assert.Equal(1, candidateLifetime.DisposeCount);
        Assert.Equal(0, baselineLifetime.DisposeCount);
        await using DataGenerationLease lease =
            await manager.AcquireAsync(CancellationToken.None);
        await lease.DisposeAsync();
        await manager.DisposeAsync();
        Assert.Equal(1, baselineLifetime.DisposeCount);
    }

    /// <summary>Verifies a retained historical root cannot be relabeled with a later sequence number.</summary>
    [Fact]
    public async Task PromoteManifestAsync_HistoricalIdentityReuse_IsRejectedAndSafelyAborted()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot first = await directory.PromoteFirstAsync();
        DataGenerationDescriptor secondDescriptor = directory.CreateGeneration(2);
        DataGenerationManifestSnapshot second = await directory.Store.PromoteAsync(
            secondDescriptor,
            first.ContentHash,
            CancellationToken.None);
        DataGenerationManifestSnapshot restored = await directory.Store.RestoreAsync(
            first,
            second.ContentHash,
            CancellationToken.None);
        DataGenerationDescriptor relabeled = new(
            secondDescriptor.GenerationId,
            generationNumber: 3,
            secondDescriptor.RootPath);

        DataGenerationStoreException directFailure =
            await Assert.ThrowsAsync<DataGenerationStoreException>(
                () => directory.Store.PromoteAsync(
                    relabeled,
                    restored.ContentHash,
                    CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.InvalidDescriptor, directFailure.Error);
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(restored);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            restored.ContentHash,
            CancellationToken.None);
        transition.Stage(new DataGenerationScope(relabeled, candidateLifetime));
        DataGenerationStoreException transitionFailure =
            await Assert.ThrowsAsync<DataGenerationStoreException>(
                () => transition.PromoteManifestAsync(
                    directory.Store,
                    CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.InvalidDescriptor, transitionFailure.Error);
        Assert.Equal(1, candidateLifetime.DisposeCount);
        DataGenerationManifestSnapshot durable =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(restored.ContentHash, durable.ContentHash);
        Assert.Equal(restored.ContentHash, manager.CurrentManifest.ContentHash);
        await using DataGenerationLease lease =
            await manager.AcquireAsync(CancellationToken.None);
        await lease.DisposeAsync();
        await manager.DisposeAsync();
    }

    private static DataGenerationManager CreateManager(
        DataGenerationManifestSnapshot snapshot,
        IAsyncDisposable? lifetime = null)
    {
        DataGenerationManager manager = new();
        manager.Initialize(snapshot, new DataGenerationScope(snapshot.Descriptor, lifetime));
        return manager;
    }
}
