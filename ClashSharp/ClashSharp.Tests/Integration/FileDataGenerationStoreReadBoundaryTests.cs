using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies bounded, share-delete-safe current-manifest reads.</summary>
public sealed class FileDataGenerationStoreReadBoundaryTests
{
    /// <summary>Verifies an oversized manifest is rejected before allocating its full contents.</summary>
    [Fact]
    public async Task LoadCurrentAsync_OversizedManifest_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        directory.Policy.EnsureLayout();
        await File.WriteAllBytesAsync(
            directory.Policy.CurrentManifestPath,
            new byte[(64 * 1024) + 1]);

        DataGenerationStoreException exception =
            await Assert.ThrowsAsync<DataGenerationStoreException>(
                () => directory.Store.LoadCurrentAsync(CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.Corrupt, exception.Error);
    }

    /// <summary>Verifies independent readers do not block atomic replacement by a writer.</summary>
    [Fact]
    public async Task LoadCurrentAsync_ConcurrentPromotionAndRestoration_ReadsCompleteManifests()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        FileDataGenerationStore[] readers =
        [
            new(directory.RootPath),
            new(directory.RootPath),
            new(directory.RootPath),
            new(directory.RootPath),
        ];
        using CancellationTokenSource stopReaders = new();
        int completedReads = 0;
        Task[] readerTasks = readers
            .Select(reader => Task.Run(async () =>
            {
                while (!stopReaders.IsCancellationRequested)
                {
                    DataGenerationManifestSnapshot? observed =
                        await reader.LoadCurrentAsync(CancellationToken.None);
                    Assert.NotNull(observed);
                    Assert.True(
                        observed.ManifestRevision > 0
                        && observed.HighestGenerationNumber > 0);
                    Interlocked.Increment(ref completedReads);
                }
            }))
            .ToArray();

        try
        {
            const int transitionCount = 30;
            for (int index = 0; index < transitionCount; index++)
            {
                DataGenerationDescriptor candidate = directory.CreateGeneration(
                    baseline.HighestGenerationNumber + 1);
                DataGenerationManifestSnapshot promoted =
                    await directory.Store.PromoteAsync(
                        candidate,
                        baseline.ContentHash,
                        CancellationToken.None);
                baseline = await directory.Store.RestoreAsync(
                    baseline,
                    promoted.ContentHash,
                    CancellationToken.None);
            }
        }
        finally
        {
            stopReaders.Cancel();
            await Task.WhenAll(readerTasks);
        }

        Assert.True(Volatile.Read(ref completedReads) > 0);
        DataGenerationManifestSnapshot current =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(baseline.ContentHash, current.ContentHash);
    }
}
