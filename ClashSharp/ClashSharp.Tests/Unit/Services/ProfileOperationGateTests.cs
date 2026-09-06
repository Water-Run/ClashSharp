using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies profile and subscription leases serialize each key and release unused resources.</summary>
public sealed class ProfileOperationGateTests
{
    [Fact]
    public async Task EnterAsync_SameKeyWaitsWhileDifferentKeyEntersImmediately()
    {
        ProfileOperationGate gate = new();
        using IDisposable first = await gate.EnterAsync("profile-a", CancellationToken.None);

        Task<IDisposable> sameProfile = gate
            .EnterAsync("profile-a", CancellationToken.None)
            .AsTask();
        Task<IDisposable> differentProfile = gate
            .EnterAsync("profile-b", CancellationToken.None)
            .AsTask();

        Assert.False(sameProfile.IsCompleted);
        using IDisposable other = await differentProfile;

        first.Dispose();
        using IDisposable second = await sameProfile;
    }

    [Fact]
    public async Task EnterAsync_CancelledWaiterDoesNotRetainOrConsumeTheKey()
    {
        ProfileOperationGate gate = new();
        using IDisposable first = await gate.EnterAsync("profile", CancellationToken.None);
        using CancellationTokenSource cancellation = new();

        Task<IDisposable> cancelled = gate
            .EnterAsync("profile", cancellation.Token)
            .AsTask();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal(1, gate.ActiveKeyCount);
        first.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);
        using IDisposable replacement = await gate.EnterAsync(
            "profile",
            CancellationToken.None);
    }

    [Fact]
    public async Task EnterAsync_LastHolderReleasesHistoricalKeys()
    {
        ProfileOperationGate gate = new();
        for (int index = 0; index < 256; index++)
        {
            using (await gate.EnterAsync($"subscription-{index}", CancellationToken.None))
            {
                Assert.Equal(1, gate.ActiveKeyCount);
            }

            Assert.Equal(0, gate.ActiveKeyCount);
        }
    }

    [Fact]
    public async Task EnterAsync_AlreadyCancelledDoesNotRetainAKey()
    {
        ProfileOperationGate gate = new();
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gate.EnterAsync("subscription", cancellation.Token).AsTask());

        Assert.Equal(0, gate.ActiveKeyCount);
    }

    [Fact]
    public async Task Dispose_WithQueuedWaiterKeepsTheKeyUntilTheWaiterExits()
    {
        ProfileOperationGate gate = new();
        using IDisposable first = await gate.EnterAsync("subscription", CancellationToken.None);
        Task<IDisposable> waiting = gate.EnterAsync("subscription", CancellationToken.None).AsTask();

        first.Dispose();
        using IDisposable next = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, gate.ActiveKeyCount);

        next.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);
    }

    [Fact]
    public async Task Dispose_RepeatedOldLeaseDoesNotReleaseTheNextHolder()
    {
        ProfileOperationGate gate = new();
        using IDisposable first = await gate.EnterAsync("subscription", CancellationToken.None);
        first.Dispose();
        using IDisposable next = await gate.EnterAsync("subscription", CancellationToken.None);
        Task<IDisposable> waiting = gate.EnterAsync("subscription", CancellationToken.None).AsTask();

        first.Dispose();
        Assert.False(waiting.IsCompleted);

        next.Dispose();
        using IDisposable last = await waiting.WaitAsync(TimeSpan.FromSeconds(5));
        last.Dispose();
        Assert.Equal(0, gate.ActiveKeyCount);
    }

    [Fact]
    public async Task Dispose_FailedOperationReleasesTheKey()
    {
        ProfileOperationGate gate = new();
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using IDisposable lease = await gate.EnterAsync("subscription", CancellationToken.None);
            throw new InvalidOperationException("Simulated catalog commit failure.");
        });

        Assert.Equal(0, gate.ActiveKeyCount);
        using IDisposable replacement = await gate.EnterAsync("subscription", CancellationToken.None);
    }

    [Fact]
    public async Task EnterAsync_ConcurrentReentryNeverSplitsAKeyAndDrainsAllEntries()
    {
        ProfileOperationGate gate = new();
        int[] holders = new int[4];
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(20));
        TaskCompletionSource start = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Task[] workers = Enumerable.Range(0, 16).Select(worker => Task.Run(async () =>
        {
            await start.Task;
            for (int iteration = 0; iteration < 64; iteration++)
            {
                int key = (worker + iteration) % holders.Length;
                using IDisposable lease = await gate.EnterAsync($"subscription-{key}", deadline.Token);
                int entered = Interlocked.Increment(ref holders[key]);
                try
                {
                    Assert.Equal(1, entered);
                    await Task.Yield();
                }
                finally
                {
                    Interlocked.Decrement(ref holders[key]);
                }
            }
        })).ToArray();

        start.SetResult();
        await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.All(holders, count => Assert.Equal(0, count));
        Assert.Equal(0, gate.ActiveKeyCount);
    }
}
