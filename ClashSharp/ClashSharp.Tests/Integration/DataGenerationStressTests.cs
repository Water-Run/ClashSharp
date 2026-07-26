using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises repeated durable and in-memory generation transitions as one state machine.</summary>
public sealed class DataGenerationStressTests
{
    /// <summary>Verifies repeated commit/rollback cycles preserve pointer and disposal invariants.</summary>
    [Fact]
    public async Task CommitAndRollback_RepeatedTransitions_RemainConsistent()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot initial = await directory.PromoteFirstAsync();
        List<TrackingAsyncDisposable> lifetimes = [new TrackingAsyncDisposable()];
        DataGenerationManager manager = new();
        manager.Initialize(
            initial,
            new DataGenerationScope(initial.Descriptor, lifetimes[0]));

        const int iterationCount = 100;
        for (int iteration = 0; iteration < iterationCount; iteration++)
        {
            DataGenerationManifestSnapshot baseline = manager.CurrentManifest;
            DataGenerationLease[] leases = await Task.WhenAll(
                Enumerable.Range(0, 16)
                    .Select(async _ => await manager.AcquireAsync(CancellationToken.None).AsTask()));
            Task<DataGenerationTransition> drain = manager
                .BeginDrainAsync(baseline.ContentHash, CancellationToken.None)
                .AsTask();
            await Task.WhenAll(leases.Select(lease => lease.DisposeAsync().AsTask()));
            DataGenerationTransition transition = await drain;

            DataGenerationDescriptor candidate =
                directory.CreateGeneration(baseline.HighestGenerationNumber + 1);
            TrackingAsyncDisposable candidateLifetime = new();
            lifetimes.Add(candidateLifetime);
            transition.Stage(new DataGenerationScope(candidate, candidateLifetime));
            DataGenerationManifestSnapshot promoted = await transition.PromoteManifestAsync(
                directory.Store,
                CancellationToken.None);
            transition.SwapToPromoted();

            Assert.Equal(iteration, lifetimes.Sum(static lifetime => lifetime.DisposeCount));
            if (iteration % 2 == 0)
            {
                await transition.CommitAsync();
            }
            else
            {
                await transition.RestoreBaselineAsync(
                    directory.Store,
                    CancellationToken.None);
            }

            DataGenerationManifestSnapshot durable =
                (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
            Assert.Equal(durable.ContentHash, manager.CurrentManifest.ContentHash);
            Assert.Equal(iteration + 1, lifetimes.Sum(static lifetime => lifetime.DisposeCount));
            await using DataGenerationLease verificationLease =
                await manager.AcquireAsync(CancellationToken.None);
            Assert.Equal(
                durable.Descriptor.GenerationId,
                verificationLease.Descriptor.GenerationId);
        }

        await manager.DisposeAsync();
        Assert.Equal(
            iterationCount + 1,
            lifetimes.Sum(static lifetime => lifetime.DisposeCount));
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
