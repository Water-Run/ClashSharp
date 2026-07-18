using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Supervision;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Tests.Unit.Supervision;

/// <summary>Verifies deterministic retry, health, and lifecycle semantics for supervised loops.</summary>
public sealed class SupervisedLoopTests
{
    /// <summary>Verifies the required retry sequence remains exact when tests inject zero jitter.</summary>
    [Fact]
    public async Task Failures_UseExactBoundedBackoffSequence()
    {
        AutoAdvanceSupervisorClock clock = new();
        SupervisedLoop loop = CreateLoop(
            clock,
            async (_, cancellationToken) =>
            {
                if (_ <= 6)
                {
                    throw new IOException("storage unavailable");
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await loop.StartAsync(CancellationToken.None);
        await clock.WaitForDelayCountAsync(6);
        await loop.StopAsync(CancellationToken.None);

        Assert.Equal(
            [1d, 2d, 5d, 10d, 30d, 30d],
            clock.Delays.Take(6).Select(delay => delay.TotalSeconds));
    }

    /// <summary>Verifies the fifth consecutive failure publishes degraded health.</summary>
    [Fact]
    public async Task FifthConsecutiveFailure_ChangesHealthToDegraded()
    {
        AutoAdvanceSupervisorClock clock = new();
        ConcurrentQueue<SupervisorHealth> observed = new();
        SupervisedLoop loop = CreateLoop(
            clock,
            async (iteration, cancellationToken) =>
            {
                if (iteration <= 5)
                {
                    throw new HttpRequestException("controller unavailable");
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            observed.Enqueue);

        await loop.StartAsync(CancellationToken.None);
        await clock.WaitForDelayCountAsync(5);

        SupervisorHealth degraded = Assert.Single(observed, health =>
            health.State == SupervisorHealthState.Degraded);
        Assert.Equal(5, degraded.ConsecutiveFailureCount);
        Assert.Equal("supervisor.http", degraded.ErrorCode);

        await loop.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies sustained failure duration can degrade health before five failures.</summary>
    [Fact]
    public async Task FailureAtSixtySecondBoundary_ChangesHealthToDegraded()
    {
        AutoAdvanceSupervisorClock clock = new();
        ConcurrentQueue<SupervisorHealth> observed = new();
        SupervisedLoop loop = CreateLoop(
            clock,
            async (iteration, cancellationToken) =>
            {
                if (iteration == 1)
                {
                    throw new IOException("first");
                }

                if (iteration == 2)
                {
                    clock.Advance(TimeSpan.FromSeconds(59));
                    throw new IOException("second");
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            observed.Enqueue);

        await loop.StartAsync(CancellationToken.None);
        await clock.WaitForDelayCountAsync(2);

        SupervisorHealth degraded = Assert.Single(observed, health =>
            health.State == SupervisorHealthState.Degraded);
        Assert.Equal(2, degraded.ConsecutiveFailureCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, degraded.FirstFailureAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(60), degraded.LastFailureAt);

        await loop.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies two normally spaced successes are required to restore healthy state.</summary>
    [Fact]
    public async Task TwoSuccessesAfterFailure_TransitionThroughRecoveringToHealthy()
    {
        AutoAdvanceSupervisorClock clock = new();
        ConcurrentQueue<SupervisorHealth> observed = new();
        SupervisedLoop loop = CreateLoop(
            clock,
            async (iteration, cancellationToken) =>
            {
                if (iteration == 1)
                {
                    throw new JsonException("invalid controller payload");
                }

                if (iteration > 3)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            },
            observed.Enqueue,
            normalInterval: TimeSpan.FromSeconds(7));

        await loop.StartAsync(CancellationToken.None);
        await clock.WaitForDelayCountAsync(3);

        SupervisorHealth recovering = Assert.Single(observed, health =>
            health.State == SupervisorHealthState.Recovering);
        Assert.Equal(1, recovering.ConsecutiveSuccessCount);
        Assert.Equal("supervisor.json", recovering.ErrorCode);

        SupervisorHealth healthy = observed.Last(health => health.State == SupervisorHealthState.Healthy);
        Assert.Equal(2, healthy.ConsecutiveSuccessCount);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(8), healthy.LastSuccessAt);
        Assert.Null(healthy.ErrorCode);
        Assert.Equal([1d, 7d, 7d], clock.Delays.Take(3).Select(delay => delay.TotalSeconds));

        await loop.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies a recovery relapse is degraded immediately and probes again after the capped delay.</summary>
    [Fact]
    public async Task FailureWhileRecovering_ReturnsToDegradedWithCappedDelay()
    {
        AutoAdvanceSupervisorClock clock = new();
        ConcurrentQueue<SupervisorHealth> observed = new();
        SupervisedLoop loop = CreateLoop(
            clock,
            async (iteration, cancellationToken) =>
            {
                if (iteration is 1 or 3)
                {
                    throw new InvalidOperationException("unexpected state");
                }

                if (iteration > 3)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
            },
            observed.Enqueue,
            normalInterval: TimeSpan.FromSeconds(4));

        await loop.StartAsync(CancellationToken.None);
        await clock.WaitForDelayCountAsync(3);

        SupervisorHealth relapse = observed.Last(health => health.State == SupervisorHealthState.Degraded);
        Assert.Equal(1, relapse.ConsecutiveFailureCount);
        Assert.Equal(0, relapse.ConsecutiveSuccessCount);
        Assert.Equal("supervisor.unexpected", relapse.ErrorCode);
        Assert.Equal([1d, 4d, 30d], clock.Delays.Take(3).Select(delay => delay.TotalSeconds));

        await loop.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies a failure snapshot contains every required diagnostic field.</summary>
    [Fact]
    public async Task FirstFailure_PublishesCompleteHealthSnapshot()
    {
        AutoAdvanceSupervisorClock clock = new();
        TaskCompletionSource<SupervisorHealth> failureObserved = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SupervisedLoop loop = CreateLoop(
            clock,
            async (iteration, cancellationToken) =>
            {
                if (iteration == 1)
                {
                    throw new HttpRequestException("offline");
                }

                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            },
            health =>
            {
                if (health.State == SupervisorHealthState.Retrying)
                {
                    failureObserved.TrySetResult(health);
                }
            });

        await loop.StartAsync(CancellationToken.None);
        SupervisorHealth health = await failureObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(SupervisorHealthState.Retrying, health.State);
        Assert.Equal(1, health.ConsecutiveFailureCount);
        Assert.Equal(0, health.ConsecutiveSuccessCount);
        Assert.Equal(DateTimeOffset.UnixEpoch, health.FirstFailureAt);
        Assert.Equal(DateTimeOffset.UnixEpoch, health.LastFailureAt);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(1), health.NextAttemptAt);
        Assert.Equal("supervisor.http", health.ErrorCode);
        Assert.Null(health.LastSuccessAt);

        await loop.StopAsync(CancellationToken.None);
    }

    /// <summary>Verifies known operational exceptions receive stable codes and all other exceptions are contained.</summary>
    [Theory]
    [MemberData(nameof(FailureCodeCases))]
    public void FailureClassifier_ReturnsStableCode(Exception exception, string expectedCode)
    {
        Assert.Equal(expectedCode, SupervisorFailureClassifier.Classify(exception));
    }

    /// <summary>Verifies production jitter never leaves the required plus-or-minus ten percent range.</summary>
    [Fact]
    public void BackoffPolicy_ProductionJitterIsBounded()
    {
        SupervisorBackoffPolicy low = new(() => -1d);
        SupervisorBackoffPolicy high = new(() => 1d);

        Assert.Equal(TimeSpan.FromSeconds(27), low.GetDelay(5));
        Assert.Equal(TimeSpan.FromSeconds(33), high.GetDelay(5));
        Assert.InRange(
            SupervisorBackoffPolicy.CreateProduction("connection-sampling").GetDelay(5),
            TimeSpan.FromSeconds(27),
            TimeSpan.FromSeconds(33));
    }

    /// <summary>Verifies quiescence is stopped health and awaits the active iteration before returning.</summary>
    [Fact]
    public async Task QuiesceAndResume_AwaitInFlightWorkAndRestorePriorState()
    {
        AutoAdvanceSupervisorClock clock = new();
        TaskCompletionSource<object?> firstStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<object?> releaseFirst = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<object?> resumedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int calls = 0;
        SupervisedLoop loop = CreateLoop(
            clock,
            async (_, cancellationToken) =>
            {
                int current = Interlocked.Increment(ref calls);
                if (current == 1)
                {
                    firstStarted.TrySetResult(null);
                    await releaseFirst.Task;
                    return;
                }

                resumedStarted.TrySetResult(null);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            });

        await loop.StartAsync(CancellationToken.None);
        await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.StartAsync(CancellationToken.None);
        Assert.Equal(1, Volatile.Read(ref calls));

        Task<QuiescedState> quiescing = loop.QuiesceAsync(CancellationToken.None);
        await WaitUntilAsync(() => loop.Health.State == SupervisorHealthState.Stopped);
        Assert.False(quiescing.IsCompleted);

        releaseFirst.TrySetResult(null);
        QuiescedState priorState = await quiescing;
        Assert.True(priorState.WasRunning);
        Assert.Equal(SupervisorHealthState.Stopped, loop.Health.State);
        QuiescedState alreadyQuiesced = await loop.QuiesceAsync(CancellationToken.None);
        Assert.False(alreadyQuiesced.WasRunning);

        await loop.ResumeAsync(priorState, CancellationToken.None);
        await resumedStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await loop.ResumeAsync(priorState, CancellationToken.None);
        Assert.Equal(2, Volatile.Read(ref calls));

        await loop.StopAsync(CancellationToken.None);
        await loop.StopAsync(CancellationToken.None);
        await loop.StartAsync(CancellationToken.None);
        int callsAfterStop = Volatile.Read(ref calls);
        await Task.Delay(30);
        Assert.Equal(callsAfterStop, Volatile.Read(ref calls));
        Assert.Equal(SupervisorHealthState.Stopped, loop.Health.State);
    }

    /// <summary>Provides exception classification cases required by the runtime contract.</summary>
    public static TheoryData<Exception, string> FailureCodeCases => new()
    {
        { new SqliteException("database unavailable", 5), "supervisor.sqlite" },
        { new IOException("disk unavailable"), "supervisor.io" },
        { new HttpRequestException("controller unavailable"), "supervisor.http" },
        { new JsonException("invalid payload"), "supervisor.json" },
        { new InvalidOperationException("bug"), "supervisor.unexpected" },
    };

    private static SupervisedLoop CreateLoop(
        ISupervisorClock clock,
        Func<int, CancellationToken, Task> iteration,
        Action<SupervisorHealth>? healthChanged = null,
        TimeSpan? normalInterval = null)
    {
        int iterationCount = 0;
        return new SupervisedLoop(
            "test-loop",
            cancellationToken => iteration(Interlocked.Increment(ref iterationCount), cancellationToken),
            () => normalInterval ?? TimeSpan.FromSeconds(10),
            clock,
            new SupervisorBackoffPolicy(() => 0d),
            healthChanged);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class AutoAdvanceSupervisorClock : ISupervisorClock
    {
        private readonly object _sync = new();
        private readonly List<TimeSpan> _delays = [];
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public DateTimeOffset UtcNow
        {
            get
            {
                lock (_sync)
                {
                    return _utcNow;
                }
            }
        }

        public IReadOnlyList<TimeSpan> Delays
        {
            get
            {
                lock (_sync)
                {
                    return [.. _delays];
                }
            }
        }

        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            lock (_sync)
            {
                _delays.Add(delay);
                _utcNow += delay;
            }

            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
        }

        public void Advance(TimeSpan duration)
        {
            lock (_sync)
            {
                _utcNow += duration;
            }
        }

        public async Task WaitForDelayCountAsync(int count)
        {
            await WaitUntilAsync(() => Delays.Count >= count);
        }
    }
}
