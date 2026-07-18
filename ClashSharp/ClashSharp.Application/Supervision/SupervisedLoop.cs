using ClashSharp.ApplicationModel.Lifecycle;

namespace ClashSharp.ApplicationModel.Supervision;

/// <summary>Runs one retrying background operation under awaited lifecycle control.</summary>
/// <remarks>
/// The supervisor owns exactly one loop task and cancellation source. Iteration failures are
/// converted to health transitions and never escape through the owned loop task.
/// </remarks>
public sealed class SupervisedLoop : IRuntimeParticipant
{
    private static readonly TimeSpan DegradedAfter = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly Func<CancellationToken, Task> _iteration;
    private readonly Func<TimeSpan> _normalInterval;
    private readonly ISupervisorClock _clock;
    private readonly SupervisorBackoffPolicy _backoff;
    private readonly Action<SupervisorHealth>? _healthChanged;
    private readonly Func<TimeSpan>? _initialDelay;

    private SupervisorHealth _health = SupervisorHealth.Stopped;
    private CancellationTokenSource? _loopCancellation;
    private Task? _loopTask;
    private bool _permanentlyStopped;

    /// <summary>Initializes a supervised loop.</summary>
    /// <param name="name">Stable participant and diagnostic name.</param>
    /// <param name="iteration">One complete unit of supervised work.</param>
    /// <param name="normalInterval">Supplies the interval after successful iterations.</param>
    /// <param name="clock">Injected time and delay source.</param>
    /// <param name="backoff">Retry delay policy.</param>
    /// <param name="healthChanged">Optional synchronous health observer.</param>
    /// <param name="initialDelay">Optional delay supplier used before the first iteration of each run.</param>
    public SupervisedLoop(
        string name,
        Func<CancellationToken, Task> iteration,
        Func<TimeSpan> normalInterval,
        ISupervisorClock clock,
        SupervisorBackoffPolicy backoff,
        Action<SupervisorHealth>? healthChanged = null,
        Func<TimeSpan>? initialDelay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name;
        _iteration = iteration ?? throw new ArgumentNullException(nameof(iteration));
        _normalInterval = normalInterval ?? throw new ArgumentNullException(nameof(normalInterval));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _backoff = backoff ?? throw new ArgumentNullException(nameof(backoff));
        _healthChanged = healthChanged;
        _initialDelay = initialDelay;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <summary>Gets the most recent immutable health snapshot.</summary>
    public SupervisorHealth Health => Volatile.Read(ref _health);

    /// <summary>Gets whether the owned loop task is active.</summary>
    public bool IsRunning => Volatile.Read(ref _loopTask) is { IsCompleted: false };

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_permanentlyStopped || _loopTask is { IsCompleted: false })
            {
                return;
            }

            CleanupCompletedLoop();
            StartCore();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool wasRunning = _loopTask is { IsCompleted: false };
            if (!wasRunning)
            {
                PublishStopped();
                CleanupCompletedLoop();
                return new QuiescedState(false);
            }

            Task stoppingTask = _loopTask!;
            _loopCancellation!.Cancel();
            PublishStopped();
            await stoppingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            PublishStopped();
            CleanupCompletedLoop();
            return new QuiescedState(true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(priorState);
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_permanentlyStopped || !priorState.WasRunning)
            {
                return;
            }

            if (_loopTask is { IsCompleted: false } stoppingTask)
            {
                if (Health.State != SupervisorHealthState.Stopped)
                {
                    return;
                }

                await stoppingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            CleanupCompletedLoop();
            StartCore();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _permanentlyStopped = true;
            Task? stoppingTask = _loopTask;
            _loopCancellation?.Cancel();
            PublishStopped();
            if (stoppingTask is not null)
            {
                await stoppingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            PublishStopped();
            CleanupCompletedLoop();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void StartCore()
    {
        CancellationTokenSource cancellation = new();
        _loopCancellation = cancellation;
        Publish(new SupervisorHealth(
            SupervisorHealthState.Healthy,
            0,
            0,
            null,
            null,
            null,
            null,
            null));
        _loopTask = RunAsync(cancellation.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Yield();
        if (_initialDelay is not null)
        {
            TimeSpan delay = GetValidatedInterval(_initialDelay(), "initial");
            DateTimeOffset scheduledAt = _clock.UtcNow;
            Publish(Health with { NextAttemptAt = scheduledAt + delay });
            try
            {
                await _clock.DelayAsync(delay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            SetHealth(Health with { NextAttemptAt = null }, notify: false);
            TimeSpan nextDelay;
            try
            {
                await _iteration(cancellationToken).ConfigureAwait(false);
                DateTimeOffset completedAt = _clock.UtcNow;
                SupervisorHealth succeeded = CreateSuccessHealth(Health, completedAt);
                nextDelay = GetNormalInterval();
                Publish(succeeded with { NextAttemptAt = completedAt + nextDelay });
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                DateTimeOffset failedAt = _clock.UtcNow;
                SupervisorHealth previous = Health;
                bool recoveryRelapse = previous.State == SupervisorHealthState.Recovering;
                SupervisorHealth failed = CreateFailureHealth(previous, failedAt, exception, recoveryRelapse);
                nextDelay = _backoff.GetDelay(failed.ConsecutiveFailureCount, recoveryRelapse);
                Publish(failed with { NextAttemptAt = failedAt + nextDelay });
            }

            try
            {
                await _clock.DelayAsync(nextDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private SupervisorHealth CreateSuccessHealth(SupervisorHealth previous, DateTimeOffset completedAt)
    {
        int successes = previous.ConsecutiveSuccessCount == int.MaxValue
            ? int.MaxValue
            : previous.ConsecutiveSuccessCount + 1;
        if (previous.State is SupervisorHealthState.Retrying or SupervisorHealthState.Degraded)
        {
            return previous with
            {
                State = SupervisorHealthState.Recovering,
                ConsecutiveFailureCount = 0,
                ConsecutiveSuccessCount = 1,
                NextAttemptAt = null,
                LastSuccessAt = completedAt,
            };
        }

        bool recovered = previous.State == SupervisorHealthState.Recovering && successes >= 2;
        return previous with
        {
            State = recovered || previous.State == SupervisorHealthState.Healthy
                ? SupervisorHealthState.Healthy
                : previous.State,
            ConsecutiveFailureCount = 0,
            ConsecutiveSuccessCount = successes,
            FirstFailureAt = recovered ? null : previous.FirstFailureAt,
            LastFailureAt = recovered ? null : previous.LastFailureAt,
            NextAttemptAt = null,
            ErrorCode = recovered ? null : previous.ErrorCode,
            LastSuccessAt = completedAt,
        };
    }

    private static SupervisorHealth CreateFailureHealth(
        SupervisorHealth previous,
        DateTimeOffset failedAt,
        Exception exception,
        bool recoveryRelapse)
    {
        int failures = recoveryRelapse
            ? 1
            : Math.Min(previous.ConsecutiveFailureCount + 1, int.MaxValue);
        DateTimeOffset firstFailure = recoveryRelapse || previous.FirstFailureAt is null
            ? failedAt
            : previous.FirstFailureAt.Value;
        bool degraded = recoveryRelapse
            || previous.State == SupervisorHealthState.Degraded
            || failures >= 5
            || failedAt - firstFailure >= DegradedAfter;

        return new SupervisorHealth(
            degraded ? SupervisorHealthState.Degraded : SupervisorHealthState.Retrying,
            failures,
            0,
            firstFailure,
            failedAt,
            null,
            SupervisorFailureClassifier.Classify(exception),
            previous.LastSuccessAt);
    }

    private TimeSpan GetNormalInterval()
    {
        return GetValidatedInterval(_normalInterval(), "normal");
    }

    private static TimeSpan GetValidatedInterval(TimeSpan interval, string intervalKind)
    {
        if (interval < TimeSpan.Zero)
        {
            throw new InvalidOperationException($"A supervisor {intervalKind} interval cannot be negative.");
        }

        return interval;
    }

    private void PublishStopped()
    {
        SupervisorHealth previous = Health;
        Publish(previous with
        {
            State = SupervisorHealthState.Stopped,
            NextAttemptAt = null,
        });
    }

    private void Publish(SupervisorHealth health)
    {
        SetHealth(health, notify: true);
    }

    private void SetHealth(SupervisorHealth health, bool notify)
    {
        Volatile.Write(ref _health, health);
        if (!notify || _healthChanged is null)
        {
            return;
        }

        try
        {
            _healthChanged(health);
        }
        catch
        {
            // Health observers cannot own or terminate the supervised task.
        }
    }

    private void CleanupCompletedLoop()
    {
        if (_loopTask is not null && !_loopTask.IsCompleted)
        {
            return;
        }

        _loopTask = null;
        _loopCancellation?.Dispose();
        _loopCancellation = null;
    }
}
