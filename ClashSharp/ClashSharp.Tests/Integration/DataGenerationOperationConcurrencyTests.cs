using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies durable store operations are exclusive with transition resolution and disposal.</summary>
public sealed class DataGenerationOperationConcurrencyTests
{
    /// <summary>Verifies an abort cannot observe a stale baseline while promotion is in flight.</summary>
    [Fact]
    public async Task PromoteManifestAsync_InFlight_RejectsConcurrentAbortWithoutSplitBrain()
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
            DataGenerationFaultPoint.BeforeManifestPromotion);
        FileDataGenerationStore blockingStore = new(directory.RootPath, injector);

        Task<DataGenerationManifestSnapshot> promotion =
            transition.PromoteManifestAsync(blockingStore, CancellationToken.None);
        await injector.Entered;
        DataGenerationManagerException abortFailure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.AbortAsync(
                    directory.Store,
                    CancellationToken.None).AsTask());

        Assert.Equal(DataGenerationManagerError.InvalidTransition, abortFailure.Error);
        Assert.Equal(0, candidateLifetime.DisposeCount);
        injector.Release();
        DataGenerationManifestSnapshot promoted = await promotion;
        transition.SwapToPromoted();
        await transition.RestoreBaselineAsync(directory.Store, CancellationToken.None);

        DataGenerationManifestSnapshot durable =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(baseline.Descriptor.GenerationId, durable.Descriptor.GenerationId);
        Assert.Equal(baseline.Descriptor.GenerationId, manager.CurrentManifest.Descriptor.GenerationId);
        Assert.Equal(promoted.HighestGenerationNumber, durable.HighestGenerationNumber);
        Assert.Equal(1, candidateLifetime.DisposeCount);
        await manager.DisposeAsync();
    }

    /// <summary>Verifies manager disposal waits until an in-flight promotion reaches classification.</summary>
    [Fact]
    public async Task DisposeAsync_DuringPromotion_WaitsForDurableOperation()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        CountingAsyncDisposable baselineLifetime = new();
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline, baselineLifetime);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        transition.Stage(new DataGenerationScope(candidate, candidateLifetime));
        BlockingDataGenerationFaultInjector injector = new(
            DataGenerationFaultPoint.BeforeManifestPromotion);
        FileDataGenerationStore blockingStore = new(directory.RootPath, injector);

        Task<DataGenerationManifestSnapshot> promotion =
            transition.PromoteManifestAsync(blockingStore, CancellationToken.None);
        await injector.Entered;
        Task disposal = manager.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.Equal(0, baselineLifetime.DisposeCount);
        Assert.Equal(0, candidateLifetime.DisposeCount);
        injector.Release();
        DataGenerationManifestSnapshot promoted = await promotion;
        await disposal;

        DataGenerationManifestSnapshot durable =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(promoted.ContentHash, durable.ContentHash);
        Assert.Equal(1, baselineLifetime.DisposeCount);
        Assert.Equal(1, candidateLifetime.DisposeCount);
        DataGenerationManagerException admission =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                async () => await manager.AcquireAsync(CancellationToken.None));
        Assert.Equal(DataGenerationManagerError.Disposed, admission.Error);
    }

    /// <summary>Verifies commit cannot cross while durable baseline restoration is in flight.</summary>
    [Fact]
    public async Task RestoreBaselineAsync_InFlight_RejectsConcurrentCommitWithoutSplitBrain()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        CountingAsyncDisposable baselineLifetime = new();
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline, baselineLifetime);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        transition.Stage(new DataGenerationScope(candidate, candidateLifetime));
        DataGenerationManifestSnapshot promoted = await transition.PromoteManifestAsync(
            directory.Store,
            CancellationToken.None);
        transition.SwapToPromoted();
        BlockingDataGenerationFaultInjector injector = new(
            DataGenerationFaultPoint.BeforeManifestPromotion);
        FileDataGenerationStore blockingStore = new(directory.RootPath, injector);

        Task<DataGenerationManifestSnapshot> restoration =
            transition.RestoreBaselineAsync(blockingStore, CancellationToken.None);
        await injector.Entered;
        DataGenerationManagerException commitFailure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.CommitAsync().AsTask());

        Assert.Equal(DataGenerationManagerError.InvalidTransition, commitFailure.Error);
        Assert.Equal(0, baselineLifetime.DisposeCount);
        Assert.Equal(0, candidateLifetime.DisposeCount);
        injector.Release();
        DataGenerationManifestSnapshot restored = await restoration;

        DataGenerationManifestSnapshot durable =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(restored.ContentHash, durable.ContentHash);
        Assert.Equal(baseline.Descriptor.GenerationId, manager.CurrentManifest.Descriptor.GenerationId);
        Assert.Equal(0, baselineLifetime.DisposeCount);
        Assert.Equal(1, candidateLifetime.DisposeCount);
        await manager.DisposeAsync();
        Assert.Equal(1, baselineLifetime.DisposeCount);
        Assert.Equal(promoted.HighestGenerationNumber, durable.HighestGenerationNumber);
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
