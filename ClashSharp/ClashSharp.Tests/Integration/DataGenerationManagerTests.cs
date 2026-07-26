using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies generation lease, drain, swap, rollback, and disposal ownership.</summary>
public sealed class DataGenerationManagerTests
{
    /// <summary>Verifies drain waits for pinned work and rejects later operations.</summary>
    [Fact]
    public async Task BeginDrainAsync_InFlightLease_WaitsAndRejectsLaterLeases()
    {
        TrackingAsyncDisposable lifetime = new();
        DataGenerationManifestSnapshot baseline = CreateSnapshot(1, 1, 1, 'a');
        DataGenerationManager manager = CreateManager(baseline, lifetime);
        DataGenerationLease lease = await manager.AcquireAsync(CancellationToken.None);

        Task<DataGenerationTransition> drainTask = manager
            .BeginDrainAsync(baseline.ContentHash, CancellationToken.None)
            .AsTask();

        Assert.False(drainTask.IsCompleted);
        DataGenerationManagerException exception =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                async () => await manager.AcquireAsync(CancellationToken.None));
        Assert.Equal(DataGenerationManagerError.Draining, exception.Error);

        await lease.DisposeAsync();
        DataGenerationTransition transition = await drainTask;

        Assert.Equal(DataGenerationScopeState.Draining, transition.BaselineScopeState);
        Assert.Equal(0, lifetime.DisposeCount);

        await transition.AbortAsync();
        await manager.DisposeAsync();
        Assert.Equal(1, lifetime.DisposeCount);
    }

    /// <summary>Verifies cancellation before drain ownership restores ordinary admission.</summary>
    [Fact]
    public async Task BeginDrainAsync_CancelledWhileWaiting_ReopensBaseline()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(1, 1, 1, 'a');
        DataGenerationManager manager = CreateManager(baseline);
        DataGenerationLease firstLease = await manager.AcquireAsync(CancellationToken.None);
        using CancellationTokenSource cancellationSource = new();
        Task<DataGenerationTransition> drainTask = manager
            .BeginDrainAsync(baseline.ContentHash, cancellationSource.Token)
            .AsTask();

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => drainTask);
        await using (DataGenerationLease secondLease =
                     await manager.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal(baseline.Descriptor.GenerationId, secondLease.Descriptor.GenerationId);
        }

        await firstLease.DisposeAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies a staged scope remains invisible until durable promotion and explicit swap.</summary>
    [Fact]
    public async Task Transition_StagePromoteSwapCommit_ChangesGenerationOnlyAtExplicitBoundaries()
    {
        TrackingAsyncDisposable oldLifetime = new();
        TrackingAsyncDisposable newLifetime = new();
        DataGenerationManifestSnapshot baseline = CreateSnapshot(1, 1, 1, 'a');
        DataGenerationManifestSnapshot promoted = CreateSnapshot(2, 2, 2, 'b');
        DataGenerationManager manager = CreateManager(baseline, oldLifetime);
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        DataGenerationScope staged = new(promoted.Descriptor, newLifetime);

        transition.Stage(staged);

        Assert.Equal(baseline.Descriptor.GenerationId, manager.CurrentManifest.Descriptor.GenerationId);
        Assert.Equal(DataGenerationScopeState.Staged, staged.State);

        transition.AcknowledgeManifestPromotion(promoted);

        Assert.Equal(baseline.Descriptor.GenerationId, manager.CurrentManifest.Descriptor.GenerationId);
        transition.SwapToPromoted();
        Assert.Equal(promoted.ContentHash, manager.CurrentManifest.ContentHash);
        Assert.Equal(DataGenerationScopeState.Active, staged.State);
        Assert.Equal(0, oldLifetime.DisposeCount);
        await Assert.ThrowsAsync<DataGenerationManagerException>(
            async () => await manager.AcquireAsync(CancellationToken.None));

        await transition.CommitAsync();

        Assert.Equal(1, oldLifetime.DisposeCount);
        Assert.Equal(0, newLifetime.DisposeCount);
        await using (DataGenerationLease lease = await manager.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal(promoted.Descriptor.GenerationId, lease.Descriptor.GenerationId);
        }

        await manager.DisposeAsync();
        Assert.Equal(1, newLifetime.DisposeCount);
    }

    /// <summary>Verifies rollback restores the old scope and only disposes the staged scope.</summary>
    [Fact]
    public async Task Transition_RollbackBeforeCommit_RestoresBaselineAndPreservesHighWater()
    {
        TrackingAsyncDisposable oldLifetime = new();
        TrackingAsyncDisposable stagedLifetime = new();
        DataGenerationManifestSnapshot baseline = CreateSnapshot(1, 1, 1, 'a');
        DataGenerationManifestSnapshot promoted = CreateSnapshot(2, 2, 2, 'b');
        DataGenerationManifestSnapshot restored = new(
            baseline.Descriptor,
            manifestRevision: 3,
            highestGenerationNumber: 2,
            new string('c', 64));
        DataGenerationManager manager = CreateManager(baseline, oldLifetime);
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        transition.Stage(new DataGenerationScope(promoted.Descriptor, stagedLifetime));
        transition.AcknowledgeManifestPromotion(promoted);
        transition.SwapToPromoted();
        await transition.RestoreBaselineAsync(
            new DataGenerationSnapshotStore(promoted, restored),
            CancellationToken.None);

        Assert.Equal(restored.ContentHash, manager.CurrentManifest.ContentHash);
        Assert.Equal(2, manager.CurrentManifest.HighestGenerationNumber);
        Assert.Equal(0, oldLifetime.DisposeCount);
        Assert.Equal(1, stagedLifetime.DisposeCount);
        await using (DataGenerationLease lease = await manager.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal(baseline.Descriptor.GenerationId, lease.Descriptor.GenerationId);
        }

        DataGenerationManagerException stale = await Assert.ThrowsAsync<DataGenerationManagerException>(
            async () => await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None));
        Assert.Equal(DataGenerationManagerError.StaleGeneration, stale.Error);

        await manager.DisposeAsync();
    }

    /// <summary>Verifies duplicate identities and nonconsecutive generation numbers cannot be staged.</summary>
    [Theory]
    [InlineData(true, 2)]
    [InlineData(false, 1)]
    [InlineData(false, 3)]
    public async Task Stage_InvalidCandidate_IsRejected(bool duplicateIdentity, long generationNumber)
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(1, 1, 1, 'a');
        DataGenerationManager manager = CreateManager(baseline);
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        Guid generationId = duplicateIdentity
            ? baseline.Descriptor.GenerationId
            : Guid.Parse("7ed95ee8-9317-4d56-b1e8-9e27a5fc3c13");
        DataGenerationScope candidate = new(CreateDescriptor(generationId, generationNumber));

        DataGenerationManagerException exception = Assert.Throws<DataGenerationManagerException>(
            () => transition.Stage(candidate));

        Assert.Equal(DataGenerationManagerError.InvalidStage, exception.Error);
        await candidate.DisposeAsync();
        await transition.AbortAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies rollback requires the exact baseline descriptor and a later manifest revision.</summary>
    [Fact]
    public async Task RollbackAsync_InvalidRestoredManifest_IsRejectedWithoutDisposingEitherScope()
    {
        TrackingAsyncDisposable oldLifetime = new();
        TrackingAsyncDisposable stagedLifetime = new();
        DataGenerationManifestSnapshot baseline = CreateSnapshot(1, 1, 1, 'a');
        DataGenerationManifestSnapshot promoted = CreateSnapshot(2, 2, 2, 'b');
        DataGenerationManager manager = CreateManager(baseline, oldLifetime);
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        transition.Stage(new DataGenerationScope(promoted.Descriptor, stagedLifetime));
        transition.AcknowledgeManifestPromotion(promoted);
        transition.SwapToPromoted();
        DataGenerationManifestSnapshot invalidRestored = CreateSnapshot(3, 3, 3, 'c');

        DataGenerationManagerException exception =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.RestoreBaselineAsync(
                    new DataGenerationSnapshotStore(promoted, invalidRestored),
                    CancellationToken.None));

        Assert.Equal(DataGenerationManagerError.InvalidTransition, exception.Error);
        Assert.Equal(0, oldLifetime.DisposeCount);
        Assert.Equal(0, stagedLifetime.DisposeCount);

        DataGenerationManifestSnapshot restored = new(
            baseline.Descriptor,
            manifestRevision: 3,
            highestGenerationNumber: 2,
            new string('d', 64));
        await transition.RestoreBaselineAsync(
            new DataGenerationSnapshotStore(promoted, restored),
            CancellationToken.None);
        await manager.DisposeAsync();
    }

    /// <summary>Verifies disposing a manager waits for active leases before scope disposal.</summary>
    [Fact]
    public async Task DisposeAsync_InFlightLease_WaitsBeforeDisposingCurrentScope()
    {
        TrackingAsyncDisposable lifetime = new();
        DataGenerationManifestSnapshot baseline = CreateSnapshot(1, 1, 1, 'a');
        DataGenerationManager manager = CreateManager(baseline, lifetime);
        DataGenerationLease lease = await manager.AcquireAsync(CancellationToken.None);

        Task disposal = manager.DisposeAsync().AsTask();

        Assert.False(disposal.IsCompleted);
        Assert.Equal(0, lifetime.DisposeCount);
        DataGenerationManagerException rejected =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                async () => await manager.AcquireAsync(CancellationToken.None));
        Assert.Equal(DataGenerationManagerError.Disposed, rejected.Error);

        await lease.DisposeAsync();
        await disposal;
        Assert.Equal(1, lifetime.DisposeCount);
    }

    private static DataGenerationManager CreateManager(
        DataGenerationManifestSnapshot snapshot,
        IAsyncDisposable? lifetime = null)
    {
        DataGenerationManager manager = new();
        manager.Initialize(snapshot, new DataGenerationScope(snapshot.Descriptor, lifetime));
        return manager;
    }

    private static DataGenerationManifestSnapshot CreateSnapshot(
        long generationNumber,
        long manifestRevision,
        long highestGenerationNumber,
        char hashCharacter)
    {
        Guid generationId = generationNumber switch
        {
            1 => Guid.Parse("a18a9d59-3908-4af0-b254-9cd4dc9382d2"),
            2 => Guid.Parse("84f0700a-3076-4aec-8313-d9bdd5031bf8"),
            _ => Guid.Parse("3d29dce4-fba2-493c-9200-3f8915e5cc8a"),
        };
        return new DataGenerationManifestSnapshot(
            CreateDescriptor(generationId, generationNumber),
            manifestRevision,
            highestGenerationNumber,
            new string(hashCharacter, 64));
    }

    private static DataGenerationDescriptor CreateDescriptor(Guid generationId, long generationNumber)
    {
        return new DataGenerationDescriptor(
            generationId,
            generationNumber,
            Path.Combine(
                Path.GetTempPath(),
                "ClashSharp-Generation-Manager",
                generationId.ToString("N")));
    }

    private sealed class TrackingAsyncDisposable : IAsyncDisposable
    {
        public int DisposeCount { get; private set; }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }
}
