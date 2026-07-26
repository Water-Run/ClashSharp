using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies invalid ordering, stale transition, and concurrent disposal boundaries.</summary>
public sealed class DataGenerationManagerBoundaryTests
{
    /// <summary>Verifies commit cannot bypass staging, durable promotion, or in-memory swap.</summary>
    [Fact]
    public async Task CommitAsync_BeforeRequiredBoundaries_IsRejectedAndCanAbort()
    {
        (DataGenerationManager manager, DataGenerationManifestSnapshot baseline) = CreateManager();
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);

        DataGenerationManagerException exception =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.CommitAsync().AsTask());

        Assert.Equal(DataGenerationManagerError.InvalidTransition, exception.Error);
        await transition.AbortAsync(
            new DataGenerationSnapshotStore(baseline),
            CancellationToken.None);
        await using DataGenerationLease lease =
            await manager.AcquireAsync(CancellationToken.None);
        await lease.DisposeAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies a promotion acknowledgement must exactly match the staged candidate.</summary>
    [Theory]
    [InlineData(3, 2, 3, 'b')]
    [InlineData(2, 3, 2, 'b')]
    [InlineData(2, 2, 3, 'b')]
    [InlineData(2, 2, 2, 'a')]
    public async Task AcknowledgeManifestPromotion_InvalidSnapshot_IsRejected(
        long generationNumber,
        long manifestRevision,
        long highWater,
        char hashCharacter)
    {
        (DataGenerationManager manager, DataGenerationManifestSnapshot baseline) = CreateManager();
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        DataGenerationDescriptor stagedDescriptor = CreateDescriptor(
            Guid.Parse("7ee58c32-9cd9-4441-887e-1e72c92c9a5b"),
            2);
        transition.Stage(new DataGenerationScope(stagedDescriptor));
        DataGenerationDescriptor acknowledgedDescriptor = generationNumber == 2
            ? stagedDescriptor
            : CreateDescriptor(Guid.Parse("1666001c-b1eb-4614-8037-099f7728c7f6"), generationNumber);
        DataGenerationManifestSnapshot invalid = new(
            acknowledgedDescriptor,
            manifestRevision,
            highWater,
            new string(hashCharacter, 64));

        DataGenerationManagerException exception = Assert.Throws<DataGenerationManagerException>(
            () => transition.AcknowledgeManifestPromotion(invalid));

        Assert.Equal(DataGenerationManagerError.InvalidTransition, exception.Error);
        await transition.AbortAsync(
            new DataGenerationSnapshotStore(baseline),
            CancellationToken.None);
        await manager.DisposeAsync();
    }

    /// <summary>Verifies a candidate cannot alias the baseline directory under a new identity.</summary>
    [Fact]
    public async Task Stage_DifferentIdentitySharingBaselineRoot_IsRejected()
    {
        (DataGenerationManager manager, DataGenerationManifestSnapshot baseline) = CreateManager();
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        DataGenerationDescriptor aliased = new(
            Guid.Parse("327c268a-4a9a-4c4b-bfd0-8ea95d18f802"),
            2,
            baseline.Descriptor.RootPath);

        DataGenerationManagerException exception = Assert.Throws<DataGenerationManagerException>(
            () => transition.Stage(new DataGenerationScope(aliased)));

        Assert.Equal(DataGenerationManagerError.InvalidStage, exception.Error);
        await transition.AbortAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies a resolved transition cannot alter a later manager state.</summary>
    [Fact]
    public async Task Transition_AfterAbort_IsStale()
    {
        (DataGenerationManager manager, DataGenerationManifestSnapshot baseline) = CreateManager();
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        await transition.AbortAsync();

        Assert.Throws<ObjectDisposedException>(
            () => transition.Stage(new DataGenerationScope(CreateDescriptor(Guid.NewGuid(), 2))));

        await manager.DisposeAsync();
    }

    /// <summary>Verifies concurrent scope disposal invokes its owned lifetime exactly once.</summary>
    [Fact]
    public async Task Scope_DisposeAsyncConcurrently_DisposesOwnedLifetimeOnce()
    {
        BlockingAsyncDisposable lifetime = new();
        DataGenerationScope scope = new(
            CreateDescriptor(Guid.Parse("d11dcb90-bd20-4204-a0c8-aa08159294af"), 1),
            lifetime);

        Task first = scope.DisposeAsync().AsTask();
        await lifetime.Entered;
        Task second = scope.DisposeAsync().AsTask();
        lifetime.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(1, lifetime.DisposeCount);
        Assert.Equal(DataGenerationScopeState.Disposed, scope.State);
    }

    /// <summary>Verifies many pinned operations drain before one exclusive transition is granted.</summary>
    [Fact]
    public async Task BeginDrainAsync_ManyConcurrentLeases_GrantsOnlyAfterLastRelease()
    {
        (DataGenerationManager manager, DataGenerationManifestSnapshot baseline) = CreateManager();
        DataGenerationLease[] leases = await Task.WhenAll(
            Enumerable.Range(0, 128)
                .Select(async _ => await manager.AcquireAsync(CancellationToken.None).AsTask()));
        Task<DataGenerationTransition> drain = manager
            .BeginDrainAsync(baseline.ContentHash, CancellationToken.None)
            .AsTask();

        await Task.WhenAll(leases.Take(127).Select(lease => lease.DisposeAsync().AsTask()));
        Assert.False(drain.IsCompleted);
        await leases[^1].DisposeAsync();
        DataGenerationTransition transition = await drain;

        await transition.AbortAsync();
        await manager.DisposeAsync();
    }

    /// <summary>Verifies repeated manager disposal shares one drain and one scope disposal.</summary>
    [Fact]
    public async Task Manager_DisposeAsyncConcurrently_DisposesCurrentScopeOnce()
    {
        BlockingAsyncDisposable lifetime = new();
        DataGenerationManifestSnapshot baseline = CreateSnapshot();
        DataGenerationManager manager = new();
        manager.Initialize(baseline, new DataGenerationScope(baseline.Descriptor, lifetime));

        Task first = manager.DisposeAsync().AsTask();
        await lifetime.Entered;
        Task second = manager.DisposeAsync().AsTask();
        lifetime.Release();
        await Task.WhenAll(first, second);

        Assert.Equal(1, lifetime.DisposeCount);
    }

    /// <summary>Verifies old-scope disposal failure is typed and keeps ordinary admission closed.</summary>
    [Fact]
    public async Task CommitAsync_OldScopeDisposalFails_ReturnsTypedFailureAndKeepsAdmissionClosed()
    {
        DataGenerationManifestSnapshot baseline = CreateSnapshot();
        DataGenerationManager manager = new();
        manager.Initialize(
            baseline,
            new DataGenerationScope(baseline.Descriptor, new ThrowingAsyncDisposable()));
        DataGenerationTransition transition =
            await manager.BeginDrainAsync(baseline.ContentHash, CancellationToken.None);
        DataGenerationDescriptor candidate =
            CreateDescriptor(Guid.Parse("b82f1169-e4cd-460d-84cd-af0d42ec47f0"), 2);
        transition.Stage(new DataGenerationScope(candidate));
        DataGenerationManifestSnapshot promoted = new(
            candidate,
            manifestRevision: 2,
            highestGenerationNumber: 2,
            new string('b', 64));
        transition.AcknowledgeManifestPromotion(promoted);
        transition.SwapToPromoted();

        DataGenerationManagerException failure =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                () => transition.CommitAsync().AsTask());
        DataGenerationManagerException admission =
            await Assert.ThrowsAsync<DataGenerationManagerException>(
                async () => await manager.AcquireAsync(CancellationToken.None));

        Assert.Equal(DataGenerationManagerError.ScopeDisposalFailed, failure.Error);
        Assert.IsType<IOException>(failure.InnerException);
        Assert.Equal(DataGenerationManagerError.Draining, admission.Error);
        await Assert.ThrowsAsync<AggregateException>(() => manager.DisposeAsync().AsTask());
    }

    private static (DataGenerationManager Manager, DataGenerationManifestSnapshot Snapshot) CreateManager()
    {
        DataGenerationManifestSnapshot snapshot = CreateSnapshot();
        DataGenerationManager manager = new();
        manager.Initialize(snapshot, new DataGenerationScope(snapshot.Descriptor));
        return (manager, snapshot);
    }

    private static DataGenerationManifestSnapshot CreateSnapshot()
    {
        return new DataGenerationManifestSnapshot(
            CreateDescriptor(Guid.Parse("9af0e11d-f721-40cf-b6a7-cbbd490d9d99"), 1),
            manifestRevision: 1,
            highestGenerationNumber: 1,
            new string('a', 64));
    }

    private static DataGenerationDescriptor CreateDescriptor(Guid id, long number)
    {
        return new DataGenerationDescriptor(
            id,
            number,
            Path.Combine(Path.GetTempPath(), "ClashSharp-Generation-Boundary", id.ToString("N")));
    }

    private sealed class BlockingAsyncDisposable : IAsyncDisposable
    {
        private readonly TaskCompletionSource<object?> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public int DisposeCount { get; private set; }

        public async ValueTask DisposeAsync()
        {
            DisposeCount++;
            _entered.TrySetResult(null);
            await _release.Task;
        }

        public void Release()
        {
            _release.TrySetResult(null);
        }
    }

    private sealed class ThrowingAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            return ValueTask.FromException(new IOException("Injected scope disposal failure."));
        }
    }
}
