using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies scope ownership and retryable transition cleanup boundaries.</summary>
public sealed class DataGenerationOwnershipBoundaryTests
{
    /// <summary>Verifies one staged scope cannot be claimed by two independent managers.</summary>
    [Fact]
    public async Task Stage_SameScopeAcrossManagers_SecondClaimIsRejected()
    {
        DataGenerationManifestSnapshot firstBaseline = CreateSnapshot(
            Guid.Parse("7e5c293c-ceac-4449-a507-542954aecb33"),
            1,
            1,
            1,
            'a');
        DataGenerationManifestSnapshot secondBaseline = CreateSnapshot(
            Guid.Parse("e4af754b-b5b1-4684-8c40-9b76f90fed5c"),
            1,
            1,
            1,
            'b');
        DataGenerationManager firstManager = CreateManager(firstBaseline);
        DataGenerationManager secondManager = CreateManager(secondBaseline);
        DataGenerationTransition firstTransition = await firstManager.BeginDrainAsync(
            firstBaseline.ContentHash,
            CancellationToken.None);
        DataGenerationTransition secondTransition = await secondManager.BeginDrainAsync(
            secondBaseline.ContentHash,
            CancellationToken.None);
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationScope sharedCandidate = new(
            CreateDescriptor(Guid.Parse("930b78a2-dd9f-4141-81ba-3a1f626267a1"), 2),
            candidateLifetime);

        firstTransition.Stage(sharedCandidate);
        DataGenerationManagerException exception =
            Assert.Throws<DataGenerationManagerException>(
                () => secondTransition.Stage(sharedCandidate));

        Assert.Equal(DataGenerationManagerError.InvalidStage, exception.Error);
        await firstTransition.AbortAsync(
            new DataGenerationSnapshotStore(firstBaseline),
            CancellationToken.None);
        await secondTransition.AbortAsync();
        Assert.Equal(1, candidateLifetime.DisposeCount);
        await firstManager.DisposeAsync();
        await secondManager.DisposeAsync();
    }

    /// <summary>Verifies retained public references cannot dispose manager-owned scopes.</summary>
    [Fact]
    public async Task DisposeAsync_ClaimedScope_IsRejectedUntilOwnerDisposesIt()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(
            Guid.Parse("c4842e3f-8a0c-4c99-8fc4-68b2e877b752"),
            1,
            1,
            1,
            'a');
        CountingAsyncDisposable lifetime = new();
        DataGenerationScope scope = new(baseline.Descriptor, lifetime);
        DataGenerationManager manager = new();
        manager.Initialize(baseline, scope);
        DataGenerationLease lease = await manager.AcquireAsync(CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.DisposeAsync().AsTask());

        Assert.Equal(0, lifetime.DisposeCount);
        await lease.DisposeAsync();
        await manager.DisposeAsync();
        Assert.Equal(1, lifetime.DisposeCount);
    }

    /// <summary>Verifies a failed post-commit cleanup remains forward-only and can be retried.</summary>
    [Fact]
    public async Task CommitAsync_TransientCleanupFailure_CannotRollbackAndCanRetry()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(
            Guid.Parse("011a2478-cf89-4179-a87d-9f52361c9f68"),
            1,
            1,
            1,
            'a');
        DataGenerationManifestSnapshot promoted = CreateSnapshot(
            Guid.Parse("25739599-9a2a-4e37-99f9-552e4c42a408"),
            2,
            2,
            2,
            'b');
        FailOnceAsyncDisposable baselineLifetime = new();
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline, baselineLifetime);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        transition.Stage(new DataGenerationScope(promoted.Descriptor, candidateLifetime));
        transition.AcknowledgeManifestPromotion(promoted);
        transition.SwapToPromoted();

        DataGenerationManagerException cleanupFailure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.CommitAsync().AsTask());
        DataGenerationManagerException rollbackFailure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.RestoreBaselineAsync(
                    new DataGenerationSnapshotStore(promoted),
                    CancellationToken.None));
        DataGenerationManagerException abortFailure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.AbortAsync(
                    new DataGenerationSnapshotStore(promoted),
                    CancellationToken.None).AsTask());

        Assert.Equal(DataGenerationManagerError.ScopeDisposalFailed, cleanupFailure.Error);
        Assert.Equal(DataGenerationManagerError.InvalidTransition, rollbackFailure.Error);
        Assert.Equal(DataGenerationManagerError.InvalidTransition, abortFailure.Error);
        Assert.Equal(promoted.ContentHash, manager.CurrentManifest.ContentHash);

        await transition.CommitAsync();
        await using (DataGenerationLease lease =
                     await manager.AcquireAsync(CancellationToken.None))
        {
            Assert.Equal(promoted.Descriptor.GenerationId, lease.Descriptor.GenerationId);
        }

        Assert.Equal(2, baselineLifetime.DisposeCount);
        await manager.DisposeAsync();
        Assert.Equal(1, candidateLifetime.DisposeCount);
    }

    /// <summary>Verifies aborted candidate cleanup can resume after a transient failure.</summary>
    [Fact]
    public async Task AbortAsync_TransientCleanupFailure_RetryReopensBaseline()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(
            Guid.Parse("3b865a9b-04fc-413f-aa80-5268c90b00bc"),
            1,
            1,
            1,
            'a');
        FailOnceAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        transition.Stage(new DataGenerationScope(
            CreateDescriptor(Guid.Parse("f26ba3ca-a20c-469c-b42a-a7354bb327d3"), 2),
            candidateLifetime));

        DataGenerationManagerException failure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.AbortAsync(
                    new DataGenerationSnapshotStore(baseline),
                    CancellationToken.None).AsTask());

        Assert.Equal(DataGenerationManagerError.ScopeDisposalFailed, failure.Error);
        await transition.AbortAsync();
        await using DataGenerationLease lease =
            await manager.AcquireAsync(CancellationToken.None);
        Assert.Equal(baseline.Descriptor.GenerationId, lease.Descriptor.GenerationId);
        Assert.Equal(2, candidateLifetime.DisposeCount);
        await lease.DisposeAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies rolled-back candidate cleanup can resume without restoring twice.</summary>
    [Fact]
    public async Task RestoreBaselineAsync_TransientCleanupFailure_RetryOnlyCleansCandidate()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(
            Guid.Parse("87521809-8fbf-4947-9daf-b5dd0333fbe0"),
            1,
            1,
            1,
            'a');
        DataGenerationManifestSnapshot promoted = CreateSnapshot(
            Guid.Parse("f75fd3c8-5e26-4ee5-b60e-d4e28ab1c5e3"),
            2,
            2,
            2,
            'b');
        DataGenerationManifestSnapshot restored = new(
            baseline.Descriptor,
            manifestRevision: 3,
            highestGenerationNumber: 2,
            new string('c', 64));
        FailOnceAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        transition.Stage(new DataGenerationScope(promoted.Descriptor, candidateLifetime));
        transition.AcknowledgeManifestPromotion(promoted);
        transition.SwapToPromoted();
        DataGenerationSnapshotStore store = new(promoted, restored);

        DataGenerationManagerException failure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.RestoreBaselineAsync(store, CancellationToken.None));

        Assert.Equal(DataGenerationManagerError.ScopeDisposalFailed, failure.Error);
        DataGenerationManifestSnapshot retryResult =
            await transition.RestoreBaselineAsync(store, CancellationToken.None);
        Assert.Equal(restored.ContentHash, retryResult.ContentHash);
        Assert.Equal(2, candidateLifetime.DisposeCount);
        await using DataGenerationLease lease =
            await manager.AcquireAsync(CancellationToken.None);
        Assert.Equal(baseline.Descriptor.GenerationId, lease.Descriptor.GenerationId);
        await lease.DisposeAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies manager disposal waits for commit cleanup and never reopens admission.</summary>
    [Fact]
    public async Task DisposeAsync_DuringCommit_WaitsAndDisposesPromotedScopeOnce()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(
            Guid.Parse("22cdacda-a7cb-4ef5-937c-443a6be8adad"),
            1,
            1,
            1,
            'a');
        DataGenerationManifestSnapshot promoted = CreateSnapshot(
            Guid.Parse("a5676cb5-cf5f-4e75-badf-5b2c51e73bb3"),
            2,
            2,
            2,
            'b');
        BlockingAsyncDisposable baselineLifetime = new();
        CountingAsyncDisposable candidateLifetime = new();
        DataGenerationManager manager = CreateManager(baseline, baselineLifetime);
        DataGenerationTransition transition = await manager.BeginDrainAsync(
            baseline.ContentHash,
            CancellationToken.None);
        transition.Stage(new DataGenerationScope(promoted.Descriptor, candidateLifetime));
        transition.AcknowledgeManifestPromotion(promoted);
        transition.SwapToPromoted();

        Task commit = transition.CommitAsync().AsTask();
        await baselineLifetime.Entered;
        Task disposal = manager.DisposeAsync().AsTask();
        DataGenerationManagerException admission =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                async () => await manager.AcquireAsync(CancellationToken.None));

        Assert.Equal(DataGenerationManagerError.Disposed, admission.Error);
        Assert.False(disposal.IsCompleted);
        baselineLifetime.Release();
        await Task.WhenAll(commit, disposal);
        Assert.Equal(1, baselineLifetime.DisposeCount);
        Assert.Equal(1, candidateLifetime.DisposeCount);
    }

    /// <summary>Verifies transient shutdown cleanup failure can be retried by the manager owner.</summary>
    [Fact]
    public async Task DisposeAsync_TransientScopeFailure_SecondCallRetriesCleanup()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot(
            Guid.Parse("a3c795bd-3d35-4398-b1f3-eb369d46bab4"),
            1,
            1,
            1,
            'a');
        FailOnceAsyncDisposable lifetime = new();
        DataGenerationManager manager = CreateManager(baseline, lifetime);

        await Assert.ThrowsAsync<AggregateException>(
            () => manager.DisposeAsync().AsTask());
        DataGenerationManagerException admission =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                async () => await manager.AcquireAsync(CancellationToken.None));

        Assert.Equal(DataGenerationManagerError.Disposed, admission.Error);
        Assert.Equal(1, lifetime.DisposeCount);
        await manager.DisposeAsync();
        await manager.DisposeAsync();
        Assert.Equal(2, lifetime.DisposeCount);
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
        Guid id,
        long number,
        long revision,
        long highWater,
        char hash)
    {
        return new DataGenerationManifestSnapshot(
            CreateDescriptor(id, number),
            revision,
            highWater,
            new string(hash, 64));
    }

    private static DataGenerationDescriptor CreateDescriptor(Guid id, long number)
    {
        return new DataGenerationDescriptor(
            id,
            number,
            Path.Combine(
                Path.GetTempPath(),
                "ClashSharp-Generation-Ownership",
                id.ToString("N")));
    }
}
