using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies hashed, versioned, atomic current-generation manifest persistence.</summary>
public sealed class FileDataGenerationStoreTests
{
    /// <summary>Verifies the first manifest round-trips from its canonical location.</summary>
    [Fact]
    public async Task PromoteAsync_FirstGeneration_RoundTripsCanonicalManifest()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor first = directory.CreateGeneration(1);

        DataGenerationManifestSnapshot saved =
            await directory.Store.PromoteAsync(first, null, CancellationToken.None);
        DataGenerationManifestSnapshot? loaded =
            await directory.Store.LoadCurrentAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal(1, saved.ManifestRevision);
        Assert.Equal(1, saved.HighestGenerationNumber);
        Assert.Equal(64, saved.ContentHash.Length);
        Assert.Equal(saved.ContentHash, saved.ContentHash.ToLowerInvariant());
        Assert.Equal(saved.ContentHash, loaded.ContentHash);
        Assert.Equal(first.GenerationId, loaded.Descriptor.GenerationId);
        Assert.Equal(first.RootPath, loaded.Descriptor.RootPath);
        Assert.True(File.Exists(directory.Policy.CurrentManifestPath));
        Assert.False(
            DataGenerationPathPolicy.IsContainedBy(
                directory.Policy.GenerationsRootPath,
                directory.Policy.CurrentManifestPath));
    }

    /// <summary>Verifies generation numbers use the durable high-water mark and stale writers fail.</summary>
    [Fact]
    public async Task PromoteAsync_ExistingGeneration_RequiresNextHighWaterAndExpectedHash()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot first = await directory.PromoteFirstAsync();
        DataGenerationDescriptor second = directory.CreateGeneration(2);

        DataGenerationStoreException stale = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.PromoteAsync(
                second,
                new string('0', 64),
                CancellationToken.None));
        Assert.Equal(DataGenerationStoreError.ConcurrencyConflict, stale.Error);

        DataGenerationDescriptor skipped = directory.CreateGeneration(3);
        DataGenerationStoreException invalid = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.PromoteAsync(skipped, first.ContentHash, CancellationToken.None));
        Assert.Equal(DataGenerationStoreError.InvalidGeneration, invalid.Error);

        DataGenerationManifestSnapshot promoted =
            await directory.Store.PromoteAsync(second, first.ContentHash, CancellationToken.None);
        Assert.Equal(2, promoted.ManifestRevision);
        Assert.Equal(2, promoted.HighestGenerationNumber);

        DataGenerationStoreException duplicate = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.PromoteAsync(second, promoted.ContentHash, CancellationToken.None));
        Assert.Equal(DataGenerationStoreError.InvalidGeneration, duplicate.Error);
    }

    /// <summary>Verifies rollback advances manifest revision without reusing a generation number.</summary>
    [Fact]
    public async Task RestoreAsync_PromotedCandidate_RestoresDescriptorAndPreservesHighWater()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        DataGenerationDescriptor second = directory.CreateGeneration(2);
        DataGenerationManifestSnapshot promoted =
            await directory.Store.PromoteAsync(second, baseline.ContentHash, CancellationToken.None);

        DataGenerationManifestSnapshot restored = await directory.Store.RestoreAsync(
            baseline,
            promoted.ContentHash,
            CancellationToken.None);

        Assert.Equal(baseline.Descriptor.GenerationId, restored.Descriptor.GenerationId);
        Assert.Equal(3, restored.ManifestRevision);
        Assert.Equal(2, restored.HighestGenerationNumber);
        Assert.NotEqual(baseline.ContentHash, restored.ContentHash);
        Assert.True(Directory.Exists(baseline.Descriptor.RootPath));
        Assert.True(Directory.Exists(promoted.Descriptor.RootPath));

        DataGenerationDescriptor third = directory.CreateGeneration(3);
        DataGenerationManifestSnapshot next =
            await directory.Store.PromoteAsync(third, restored.ContentHash, CancellationToken.None);
        Assert.Equal(4, next.ManifestRevision);
        Assert.Equal(3, next.HighestGenerationNumber);
    }

    /// <summary>Verifies every write cut leaves the complete old or complete new manifest.</summary>
    [Theory]
    [InlineData(DataGenerationFaultPoint.AfterTemporaryFlush, 1)]
    [InlineData(DataGenerationFaultPoint.BeforeManifestPromotion, 1)]
    [InlineData(DataGenerationFaultPoint.AfterManifestPromotion, 2)]
    public async Task PromoteAsync_InjectedFailure_PreservesOneCompleteManifest(
        DataGenerationFaultPoint faultPoint,
        long expectedGenerationNumber)
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot first = await directory.PromoteFirstAsync();
        DataGenerationDescriptor second = directory.CreateGeneration(2);
        FileDataGenerationStore failingStore = new(
            directory.RootPath,
            new ThrowingFaultInjector(faultPoint));

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => failingStore.PromoteAsync(second, first.ContentHash, CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.Unavailable, exception.Error);
        DataGenerationManifestSnapshot? loaded =
            await directory.Store.LoadCurrentAsync(CancellationToken.None);
        Assert.NotNull(loaded);
        Assert.Equal(expectedGenerationNumber, loaded.Descriptor.GenerationNumber);
        Assert.Equal(expectedGenerationNumber, loaded.HighestGenerationNumber);
        Assert.Empty(Directory.EnumerateFiles(
            directory.Policy.DataRootPath,
            $".{DataGenerationPathPolicy.CurrentManifestFileName}.*.tmp"));
    }

    /// <summary>Verifies restoring the current or an unrelated snapshot is rejected.</summary>
    [Fact]
    public async Task RestoreAsync_NonBaselineSnapshot_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot first = await directory.PromoteFirstAsync();

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.RestoreAsync(first, first.ContentHash, CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.InvalidGeneration, exception.Error);
        Assert.Equal(
            first.ContentHash,
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    /// <summary>Verifies corrupt or unsupported envelope bytes are never treated as no manifest.</summary>
    [Theory]
    [InlineData("{\"schemaVersion\":99}", DataGenerationStoreError.UnsupportedSchema)]
    [InlineData(
        "{\"schemaVersion\":1,\"payload\":\"e30=\",\"contentHash\":\"0000000000000000000000000000000000000000000000000000000000000000\"}",
        DataGenerationStoreError.Corrupt)]
    public async Task LoadCurrentAsync_InvalidEnvelope_ReturnsTypedFailure(
        string json,
        DataGenerationStoreError expectedError)
    {
        await using DataGenerationTestDirectory directory = new();
        directory.Policy.EnsureLayout();
        await File.WriteAllTextAsync(directory.Policy.CurrentManifestPath, json);

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.LoadCurrentAsync(CancellationToken.None));

        Assert.Equal(expectedError, exception.Error);
    }

    /// <summary>Verifies promotion validates the exact canonical generation directory.</summary>
    [Fact]
    public async Task PromoteAsync_NonCanonicalDescriptorRoot_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        Guid generationId = Guid.NewGuid();
        string wrongRoot = Path.Combine(directory.Policy.GenerationsRootPath, "wrong");
        Directory.CreateDirectory(wrongRoot);
        DataGenerationDescriptor descriptor = new(generationId, 1, wrongRoot);

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.PromoteAsync(descriptor, null, CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.UnsafePath, exception.Error);
        Assert.False(File.Exists(directory.Policy.CurrentManifestPath));
    }

    /// <summary>Verifies two store instances cannot both promote from one expected hash.</summary>
    [Fact]
    public async Task PromoteAsync_ConcurrentStoreInstances_ExactlyOneWriterAdvances()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot first = await directory.PromoteFirstAsync();
        DataGenerationDescriptor winner = directory.CreateGeneration(2);
        DataGenerationDescriptor staleCandidate = directory.CreateGeneration(2);
        PausingFaultInjector pause = new(DataGenerationFaultPoint.BeforeManifestPromotion);
        FileDataGenerationStore firstStore = new(directory.RootPath, pause);
        FileDataGenerationStore secondStore = new(directory.RootPath);
        Task<DataGenerationManifestSnapshot> firstWrite =
            firstStore.PromoteAsync(winner, first.ContentHash, CancellationToken.None);
        await pause.Entered;

        Task<DataGenerationManifestSnapshot> secondWrite =
            secondStore.PromoteAsync(staleCandidate, first.ContentHash, CancellationToken.None);
        Task earlyCompletion = await Task.WhenAny(secondWrite, Task.Delay(TimeSpan.FromMilliseconds(150)));

        Assert.NotSame(secondWrite, earlyCompletion);
        pause.Release();
        DataGenerationManifestSnapshot promoted = await firstWrite;
        DataGenerationStoreException conflict =
            await Assert.ThrowsAsync<DataGenerationStoreException>(() => secondWrite);

        Assert.Equal(DataGenerationStoreError.ConcurrencyConflict, conflict.Error);
        Assert.Equal(
            promoted.ContentHash,
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    /// <summary>Verifies cancellation while waiting for another manifest writer has no side effect.</summary>
    [Fact]
    public async Task PromoteAsync_CancelledWhileWaitingForWriter_DoesNotPromote()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot first = await directory.PromoteFirstAsync();
        DataGenerationDescriptor winner = directory.CreateGeneration(2);
        DataGenerationDescriptor cancelledCandidate = directory.CreateGeneration(2);
        PausingFaultInjector pause = new(DataGenerationFaultPoint.BeforeManifestPromotion);
        FileDataGenerationStore firstStore = new(directory.RootPath, pause);
        FileDataGenerationStore secondStore = new(directory.RootPath);
        Task<DataGenerationManifestSnapshot> firstWrite =
            firstStore.PromoteAsync(winner, first.ContentHash, CancellationToken.None);
        await pause.Entered;
        using CancellationTokenSource cancellationSource = new();
        Task<DataGenerationManifestSnapshot> cancelledWrite = secondStore.PromoteAsync(
            cancelledCandidate,
            first.ContentHash,
            cancellationSource.Token);

        cancellationSource.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledWrite);
        pause.Release();
        DataGenerationManifestSnapshot promoted = await firstWrite;
        Assert.Equal(
            promoted.ContentHash,
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    /// <summary>Verifies caller cancellation after promotion cannot skip mandatory re-read verification.</summary>
    [Fact]
    public async Task PromoteAsync_CancelledAfterPromotion_CompletesVerifiedCommit()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot first = await directory.PromoteFirstAsync();
        DataGenerationDescriptor second = directory.CreateGeneration(2);
        using CancellationTokenSource cancellationSource = new();
        CancellingAfterPromotionInjector injector = new(cancellationSource);
        FileDataGenerationStore store = new(directory.RootPath, injector);

        DataGenerationManifestSnapshot promoted =
            await store.PromoteAsync(second, first.ContentHash, cancellationSource.Token);

        Assert.True(cancellationSource.IsCancellationRequested);
        Assert.False(injector.ObservedTokenCanBeCancelled);
        Assert.Equal(
            promoted.ContentHash,
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    /// <summary>Verifies a fabricated baseline hash cannot authorize restoration.</summary>
    [Fact]
    public async Task RestoreAsync_FabricatedBaselineHash_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        DataGenerationDescriptor second = directory.CreateGeneration(2);
        DataGenerationManifestSnapshot promoted =
            await directory.Store.PromoteAsync(second, baseline.ContentHash, CancellationToken.None);
        DataGenerationManifestSnapshot fabricated = new(
            baseline.Descriptor,
            baseline.ManifestRevision,
            baseline.HighestGenerationNumber,
            new string('0', 64));

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.RestoreAsync(
                fabricated,
                promoted.ContentHash,
                CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.InvalidHash, exception.Error);
        Assert.Equal(
            promoted.ContentHash,
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!.ContentHash);
    }

    private sealed class ThrowingFaultInjector(DataGenerationFaultPoint target)
        : IDataGenerationFaultInjector
    {
        public Task InjectAsync(
            DataGenerationFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint == target)
            {
                throw new IOException("Injected data-generation fault.");
            }

            return Task.CompletedTask;
        }
    }

    private sealed class PausingFaultInjector(DataGenerationFaultPoint target)
        : IDataGenerationFaultInjector
    {
        private readonly TaskCompletionSource<object?> _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<object?> _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public async Task InjectAsync(
            DataGenerationFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint != target)
            {
                return;
            }

            _entered.TrySetResult(null);
            await _release.Task.WaitAsync(cancellationToken);
        }

        public void Release()
        {
            _release.TrySetResult(null);
        }
    }

    private sealed class CancellingAfterPromotionInjector(CancellationTokenSource source)
        : IDataGenerationFaultInjector
    {
        public bool ObservedTokenCanBeCancelled { get; private set; }

        public Task InjectAsync(
            DataGenerationFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint == DataGenerationFaultPoint.AfterManifestPromotion)
            {
                ObservedTokenCanBeCancelled = cancellationToken.CanBeCanceled;
                source.Cancel();
            }

            return Task.CompletedTask;
        }
    }
}
