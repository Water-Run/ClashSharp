using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Properties of the fair mutation gate: strict mutual exclusion, FIFO hand-off among surviving waiters under
/// cancellation at any queue position, reentrancy rejection across awaits and child tasks, and release on every
/// exit path.
/// </summary>
public sealed class FairAsyncMutationGatePropertyTests
{
    /// <summary>Verifies random arrival, cancellation and completion order never breaks exclusion or FIFO among survivors.</summary>
    /// <param name="seedOffset">Offset added to the fixed seed.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task RandomWaiters_AreExclusiveFifoAndCancelledWaitersNeverRun(int seedOffset)
    {
        Random random = new(SettingsGenerators.Seed + seedOffset);
        FairAsyncMutationGate gate = new();
        TaskCompletionSource holderEntered = NewSignal();
        TaskCompletionSource holderRelease = NewSignal();
        Task holder = gate.ExecuteAsync(
            Guid.NewGuid(),
            async (_, _) =>
            {
                holderEntered.SetResult();
                await holderRelease.Task;
            },
            CancellationToken.None);
        await holderEntered.Task;

        int count = 40;
        List<int> executionOrder = [];
        int concurrent = 0;
        int maxConcurrent = 0;
        List<(int Index, Task Task, CancellationTokenSource Source)> waiters = [];
        for (int index = 0; index < count; index++)
        {
            CancellationTokenSource source = new();
            int captured = index;
            Task task = gate.ExecuteAsync(
                Guid.NewGuid(),
                async (_, _) =>
                {
                    maxConcurrent = Math.Max(maxConcurrent, Interlocked.Increment(ref concurrent));
                    executionOrder.Add(captured);
                    await Task.Yield();
                    Interlocked.Decrement(ref concurrent);
                },
                source.Token);
            waiters.Add((index, task, source));
            Assert.Equal(index + 1, gate.QueuedCount);
        }

        HashSet<int> cancelled = [.. Enumerable.Range(0, count).Where(_ => random.Next(3) == 0)];
        int cancelledSoFar = 0;
        foreach (int index in cancelled.OrderBy(_ => random.Next()))
        {
            await waiters[index].Source.CancelAsync();
            cancelledSoFar++;
            Assert.Equal(count - cancelledSoFar, gate.QueuedCount);
        }

        Assert.Equal(count - cancelled.Count, gate.QueuedCount);
        holderRelease.SetResult();
        await holder;
        foreach ((int index, Task task, CancellationTokenSource source) in waiters)
        {
            if (cancelled.Contains(index))
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            }
            else
            {
                await task.WaitAsync(TimeSpan.FromSeconds(10));
            }

            source.Dispose();
        }

        Assert.Equal(1, maxConcurrent);
        Assert.Equal(Enumerable.Range(0, count).Where(index => !cancelled.Contains(index)), executionOrder);
        Assert.False(gate.IsHeld);
        Assert.Equal(0, gate.QueuedCount);
    }

    /// <summary>Verifies cancellation at each phase (before start, queued, running) always releases the gate for the next waiter.</summary>
    [Fact]
    public async Task CancellationAtEveryPhase_AlwaysHandsTheGateToTheNextWaiter()
    {
        foreach (string phase in new[] { "queued", "running", "before-start" })
        {
            FairAsyncMutationGate gate = new();
            using CancellationTokenSource cancellation = new();
            TaskCompletionSource firstEntered = NewSignal();
            TaskCompletionSource firstRelease = NewSignal();
            Task first = phase switch
            {
                "queued" => gate.ExecuteAsync(Guid.NewGuid(), async (_, _) => { firstEntered.SetResult(); await firstRelease.Task; }, CancellationToken.None),
                "running" => gate.ExecuteAsync(
                    Guid.NewGuid(),
                    async (_, token) =>
                    {
                        firstEntered.SetResult();
                        await firstRelease.Task.WaitAsync(token);
                    },
                    cancellation.Token),
                _ => Task.CompletedTask,
            };

            if (phase == "before-start")
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, cancellation.Token));
                Assert.False(gate.IsHeld);
                continue;
            }

            await firstEntered.Task;
            bool secondRan = false;
            using CancellationTokenSource secondCancellation = new();
            Task second = gate.ExecuteAsync(Guid.NewGuid(), (_, _) => { secondRan = true; return Task.CompletedTask; }, phase == "queued" ? secondCancellation.Token : CancellationToken.None);
            Task third = gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, CancellationToken.None);

            switch (phase)
            {
                case "queued":
                    await secondCancellation.CancelAsync();
                    firstRelease.SetResult();
                    break;
                default:
                    await cancellation.CancelAsync();
                    break;
            }

            if (phase == "running")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
            }
            else
            {
                await first;
            }

            if (phase == "queued")
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
                Assert.False(secondRan);
            }
            else
            {
                await second.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(secondRan);
            }

            await third.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(gate.IsHeld);
            Assert.Equal(0, gate.QueuedCount);
        }
    }

    /// <summary>Verifies the same logical flow can never re-enter, but sequential and parallel flows are unaffected.</summary>
    [Fact]
    public async Task Reentrancy_IsRejectedForTheLogicalFlowButNotForSequentialOrParallelFlows()
    {
        FairAsyncMutationGate gate = new();
        Guid outer = Guid.NewGuid();

        MutationReentrancyException direct = await Assert.ThrowsAsync<MutationReentrancyException>(
            () => gate.ExecuteAsync(
                outer,
                (_, _) => gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, CancellationToken.None),
                CancellationToken.None));
        Assert.Equal(outer, direct.OwnerOperationId);

        MutationReentrancyException afterAwait = await Assert.ThrowsAsync<MutationReentrancyException>(
            () => gate.ExecuteAsync(
                outer,
                async (_, token) =>
                {
                    await Task.Delay(1, token);
                    await Task.Yield();
                    await gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, CancellationToken.None);
                },
                CancellationToken.None));
        Assert.Equal(outer, afterAwait.OwnerOperationId);

        MutationReentrancyException fromChildTask = await Assert.ThrowsAsync<MutationReentrancyException>(
            () => gate.ExecuteAsync(
                outer,
                async (_, token) => await Task.Run(() => gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, CancellationToken.None), token),
                CancellationToken.None));
        Assert.Equal(outer, fromChildTask.OwnerOperationId);
        Assert.False(gate.IsHeld);

        int ran = 0;
        for (int index = 0; index < 5; index++)
        {
            await gate.ExecuteAsync(Guid.NewGuid(), (_, _) => { ran++; return Task.CompletedTask; }, CancellationToken.None);
        }

        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => gate.ExecuteAsync(Guid.NewGuid(), (_, _) => { ran++; return Task.CompletedTask; }, CancellationToken.None)));
        Assert.Equal(10, ran);
        Assert.False(gate.IsHeld);
    }

    /// <summary>Verifies every exit path (success, throw, synchronous throw, invalid arguments) releases the gate.</summary>
    [Fact]
    public async Task EveryExitPath_ReleasesTheGate()
    {
        FairAsyncMutationGate gate = new();

        await gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.False(gate.IsHeld);

        await Assert.ThrowsAsync<IOException>(() => gate.ExecuteAsync(Guid.NewGuid(), async (_, _) => { await Task.Yield(); throw new IOException("boom"); }, CancellationToken.None));
        Assert.False(gate.IsHeld);

        await Assert.ThrowsAsync<IOException>(() => gate.ExecuteAsync<int>(Guid.NewGuid(), (_, _) => throw new IOException("sync boom"), CancellationToken.None));
        Assert.False(gate.IsHeld);

        int result = await gate.ExecuteAsync(Guid.NewGuid(), (_, _) => Task.FromResult(42), CancellationToken.None);
        Assert.Equal(42, result);

        await Assert.ThrowsAsync<ArgumentException>(() => gate.ExecuteAsync(Guid.Empty, (_, _) => Task.CompletedTask, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => gate.ExecuteAsync(Guid.NewGuid(), (Func<MutationContext, CancellationToken, Task>)null!, CancellationToken.None));
        Assert.False(gate.IsHeld);
        Assert.Equal(0, gate.QueuedCount);
    }

    /// <summary>Verifies an operation cancelled while another holds the gate never observes the gate being held by it.</summary>
    [Fact]
    public async Task ManyConcurrentOperationsWithRandomCancellation_NeverOverlap()
    {
        Random random = new(SettingsGenerators.Seed);
        FairAsyncMutationGate gate = new();
        int inside = 0;
        int violations = 0;
        int completed = 0;
        List<Task> tasks = [];
        List<CancellationTokenSource> sources = [];
        for (int index = 0; index < 300; index++)
        {
            CancellationTokenSource source = new();
            sources.Add(source);
            tasks.Add(Task.Run(async () =>
            {
                try
                {
                    await gate.ExecuteAsync(
                        Guid.NewGuid(),
                        async (_, _) =>
                        {
                            if (Interlocked.Increment(ref inside) != 1)
                            {
                                Interlocked.Increment(ref violations);
                            }

                            await Task.Yield();
                            Interlocked.Decrement(ref inside);
                            Interlocked.Increment(ref completed);
                        },
                        source.Token);
                }
                catch (OperationCanceledException)
                {
                }
            }));
        }

        foreach (CancellationTokenSource source in sources.Where(_ => random.Next(2) == 0))
        {
            await source.CancelAsync();
        }

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(0, violations);
        Assert.True(completed > 0);
        Assert.False(gate.IsHeld);
        Assert.Equal(0, gate.QueuedCount);
        sources.ForEach(static source => source.Dispose());
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
