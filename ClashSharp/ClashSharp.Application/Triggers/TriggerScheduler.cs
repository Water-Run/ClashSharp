using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Supplies the global runtime enablement setting without coupling scheduling to UI settings.</summary>
public interface ITriggerSchedulerSettings
{
    /// <summary>Gets whether trigger evaluation is globally enabled.</summary>
    bool IsEnabled { get; }
}

/// <summary>Publishes process-local trigger events to the scheduler.</summary>
public interface ITriggerSchedulerEventSource
{
    /// <summary>Raised synchronously when runtime state requests a trigger evaluation pass.</summary>
    event EventHandler<TriggerSchedulerEvent>? EventRaised;
}

/// <summary>Immutable typed work item accepted by the trigger scheduler.</summary>
public sealed record TriggerSchedulerEvent
{
    /// <summary>Initializes one validated periodic or runtime event.</summary>
    public TriggerSchedulerEvent(
        TriggerEventKind eventKind,
        TriggerNotificationLevel? notificationLevel = null)
    {
        if (!Enum.IsDefined(eventKind))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (notificationLevel is TriggerNotificationLevel level && !Enum.IsDefined(level))
        {
            throw new ArgumentOutOfRangeException(nameof(notificationLevel));
        }

        if ((eventKind == TriggerEventKind.NotificationRaised) != (notificationLevel is not null))
        {
            throw new ArgumentException(
                "Only notification events carry a notification level.",
                nameof(notificationLevel));
        }

        EventKind = eventKind;
        NotificationLevel = notificationLevel;
    }

    /// <summary>Gets the event kind.</summary>
    public TriggerEventKind EventKind { get; }

    /// <summary>Gets notification severity only for notification events.</summary>
    public TriggerNotificationLevel? NotificationLevel { get; }
}

/// <summary>Result of evaluating every enabled task for one accepted scheduler event.</summary>
public sealed class TriggerSchedulerEvaluationOutcome
{
    private TriggerSchedulerEvaluationOutcome(
        IEnumerable<TriggerExecution> releaseCandidates,
        string? diagnosticCode)
    {
        ArgumentNullException.ThrowIfNull(releaseCandidates);
        TriggerExecution[] executions = releaseCandidates.ToArray();
        if (executions.Any(static execution => execution is null)
            || executions.Select(static execution => execution.ExecutionId).Distinct().Count() != executions.Length)
        {
            throw new ArgumentException(
                "Release candidates must be present and unique.",
                nameof(releaseCandidates));
        }

        if (diagnosticCode is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        }

        ReleaseCandidates = Array.AsReadOnly(executions);
        DiagnosticCode = diagnosticCode;
    }

    /// <summary>Gets executions whose producer-owned evaluation leases have unwound.</summary>
    public ReadOnlyCollection<TriggerExecution> ReleaseCandidates { get; }

    /// <summary>Gets the first stable diagnostic observed during the pass, or null.</summary>
    public string? DiagnosticCode { get; }

    /// <summary>Creates a successful evaluation pass.</summary>
    public static TriggerSchedulerEvaluationOutcome Succeeded(
        IEnumerable<TriggerExecution>? releaseCandidates = null) =>
        new(releaseCandidates ?? [], null);

    /// <summary>Creates a completed pass that retained a stable diagnostic.</summary>
    public static TriggerSchedulerEvaluationOutcome WithDiagnostic(
        string diagnosticCode,
        IEnumerable<TriggerExecution>? releaseCandidates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new TriggerSchedulerEvaluationOutcome(releaseCandidates ?? [], diagnosticCode);
    }
}

/// <summary>Evaluates all current enabled tasks for one scheduler event.</summary>
public interface ITriggerSchedulerEvaluator
{
    /// <summary>Runs one complete serialized evaluation pass.</summary>
    Task<TriggerSchedulerEvaluationOutcome> EvaluateAsync(
        TriggerSchedulerEvent triggerEvent,
        CancellationToken cancellationToken);
}

/// <summary>Uses current repository definitions and the per-task coordinator for one scheduler event.</summary>
public sealed class TriggerSchedulerEvaluator : ITriggerSchedulerEvaluator
{
    private readonly ITriggerRepository _repository;
    private readonly TriggerExecutionCoordinator _coordinator;

    /// <summary>Initializes an evaluator over durable definitions and execution coordination.</summary>
    public TriggerSchedulerEvaluator(
        ITriggerRepository repository,
        TriggerExecutionCoordinator coordinator)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
    }

    /// <inheritdoc />
    public async Task<TriggerSchedulerEvaluationOutcome> EvaluateAsync(
        TriggerSchedulerEvent triggerEvent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(triggerEvent);
        TriggerPersistenceResult<TriggerRepositorySnapshot> read =
            await _repository.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSucceeded || read.Value is not TriggerRepositorySnapshot snapshot)
        {
            return TriggerSchedulerEvaluationOutcome.WithDiagnostic(
                read.Diagnostic?.Code ?? "trigger.scheduler.snapshot_unavailable");
        }

        string? diagnosticCode = null;
        List<TriggerExecution> releaseCandidates = [];
        foreach (TriggerTaskRecord task in snapshot.Tasks.Where(static task => task.Definition.IsEnabled))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                TriggerEvaluationResult result = await _coordinator.EvaluateAsync(
                    task.Definition.Id,
                    triggerEvent.EventKind,
                    triggerEvent.NotificationLevel,
                    cancellationToken).ConfigureAwait(false);
                diagnosticCode ??= GetDiagnosticCode(result);
                if (result is
                    {
                        Status: TriggerEvaluationStatus.Committed,
                        DispatchStatus: TriggerDispatchStatus.Completed,
                        Execution: not null,
                    }
                    && task.Definition.Actions[^1].Kind == TriggerActionKind.ExitApplication)
                {
                    releaseCandidates.Add(result.Execution);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                diagnosticCode ??= SupervisorFailureClassifier.Classify(exception);
            }
        }

        return diagnosticCode is null
            ? TriggerSchedulerEvaluationOutcome.Succeeded(releaseCandidates)
            : TriggerSchedulerEvaluationOutcome.WithDiagnostic(diagnosticCode, releaseCandidates);
    }

    private static string? GetDiagnosticCode(TriggerEvaluationResult result)
    {
        return result.Status is TriggerEvaluationStatus.ContextUnavailable
            or TriggerEvaluationStatus.Conflict
            or TriggerEvaluationStatus.RepositoryUnavailable
            || result.DispatchStatus == TriggerDispatchStatus.Deferred
                ? result.DiagnosticCode ?? "trigger.scheduler.evaluation_incomplete"
                : null;
    }
}

/// <summary>Owns deterministic periodic and runtime trigger scheduling as one awaited participant.</summary>
public sealed class TriggerScheduler : IRuntimeParticipant
{
    private const int ImmediateReleaseAcknowledgementAttempts = 3;

    private readonly ConcurrentQueue<TriggerSchedulerEvent> _pendingEvents = new();
    private readonly Dictionary<Guid, TriggerExecution> _pendingReleaseCandidates = [];
    private readonly Channel<byte> _wakeChannel = Channel.CreateBounded<byte>(
        new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly object _publicationLock = new();
    private readonly ITriggerSchedulerSettings _settings;
    private readonly ITriggerSchedulerEventSource _eventSource;
    private readonly ITriggerSchedulerClock _clock;
    private readonly ITriggerSchedulerEvaluator _evaluator;
    private readonly ITriggerLifecycleHandoff _lifecycleHandoff;
    private readonly Action<SupervisorHealth>? _healthChanged;

    private SupervisorHealth _health = SupervisorHealth.Stopped;
    private CancellationTokenSource? _workCancellation;
    private CancellationTokenSource? _clockCancellation;
    private Task? _runTask;
    private bool _subscribed;
    private bool _permanentlyStopped;
    private bool _pauseExitCommitted;
    private int _acceptingEvents;

    /// <summary>Initializes one host-owned trigger scheduler.</summary>
    public TriggerScheduler(
        ITriggerSchedulerSettings settings,
        ITriggerSchedulerEventSource eventSource,
        ITriggerSchedulerClock clock,
        ITriggerSchedulerEvaluator evaluator,
        ITriggerLifecycleHandoff lifecycleHandoff,
        Action<SupervisorHealth>? healthChanged = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _eventSource = eventSource ?? throw new ArgumentNullException(nameof(eventSource));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _evaluator = evaluator ?? throw new ArgumentNullException(nameof(evaluator));
        _lifecycleHandoff = lifecycleHandoff ?? throw new ArgumentNullException(nameof(lifecycleHandoff));
        _healthChanged = healthChanged;
    }

    /// <inheritdoc />
    public string Name => "trigger-supervisor";

    /// <summary>Gets the most recent immutable scheduler health.</summary>
    public SupervisorHealth Health => Volatile.Read(ref _health);

    /// <summary>Gets whether synchronous runtime publication is currently accepted.</summary>
    public bool IsAcceptingEvents => Volatile.Read(ref _acceptingEvents) == 1;

    /// <summary>Gets whether the sole owned scheduler task is active.</summary>
    public bool IsRunning => Volatile.Read(ref _runTask) is { IsCompleted: false };

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_permanentlyStopped || _runTask is { IsCompleted: false })
            {
                return;
            }

            CleanupCompletedRun();
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
            bool wasRunning = _runTask is { IsCompleted: false } && IsAcceptingEvents;
            if (!wasRunning)
            {
                CleanupCompletedRun();
                PublishStopped();
                return new QuiescedState(false);
            }

            Task stoppingTask = _runTask!;
            PauseCore();
            try
            {
                await stoppingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                try
                {
                    await RestoreInterruptedQuiescenceAsync(stoppingTask).ConfigureAwait(false);
                }
                catch (Exception restoreFailure)
                {
                    throw new AggregateException(failure, restoreFailure);
                }

                ExceptionDispatchInfo.Capture(failure).Throw();
                throw;
            }

            CleanupCompletedRun();
            PublishStopped();
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
            if (_permanentlyStopped || !priorState.WasRunning || _runTask is { IsCompleted: false })
            {
                return;
            }

            CleanupCompletedRun();
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
            Task? stoppingTask = _runTask;
            CancellationTokenSource? workCancellation = _workCancellation;
            PauseCore();
            workCancellation?.Cancel();
            SignalWork();
            if (stoppingTask is not null)
            {
                try
                {
                    await stoppingTask.WaitAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (
                    !cancellationToken.IsCancellationRequested
                    && workCancellation?.IsCancellationRequested == true
                    && !ExceptionGraphClassifier.IsProcessFatal(exception))
                {
                    // Cancellation of the participant-owned loop is successful stop completion.
                }
                catch
                {
                    CleanupCompletedRun();
                    throw;
                }
            }

            CleanupCompletedRun();
            while (_pendingEvents.TryDequeue(out _))
            {
            }

            DrainWakeChannel();
            PublishStopped();
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void StartCore()
    {
        _workCancellation = new CancellationTokenSource();
        lock (_publicationLock)
        {
            ReplaceClockCancellation();
            _pauseExitCommitted = false;
            Subscribe();
            Volatile.Write(ref _acceptingEvents, 1);
        }

        Publish(new SupervisorHealth(
            SupervisorHealthState.Healthy,
            0,
            0,
            null,
            null,
            null,
            null,
            null));
        _runTask = RunAsync(_workCancellation.Token);
    }

    private void PauseCore()
    {
        lock (_publicationLock)
        {
            Volatile.Write(ref _acceptingEvents, 0);
            Unsubscribe();
        }

        _clockCancellation?.Cancel();
        SignalWork();
    }

    private async Task RestoreInterruptedQuiescenceAsync(Task stoppingTask)
    {
        if (_permanentlyStopped)
        {
            return;
        }

        SupervisorHealth healthBeforeRestart = Health;
        bool restartRequired;
        lock (_publicationLock)
        {
            restartRequired = _pauseExitCommitted || stoppingTask.IsCompleted;
            if (!restartRequired)
            {
                ReplaceClockCancellation();
                Subscribe();
                Volatile.Write(ref _acceptingEvents, 1);
            }
        }

        if (restartRequired)
        {
            await stoppingTask.ConfigureAwait(false);
            CleanupCompletedRun();
            StartCore();
            Publish(healthBeforeRestart);
            return;
        }

        SignalWork();
    }

    private async Task RunAsync(CancellationToken workCancellationToken)
    {
        await Task.Yield();
        Task tickTask = StartTickWait();
        while (!workCancellationToken.IsCancellationRequested)
        {
            if (_pendingEvents.TryDequeue(out TriggerSchedulerEvent? triggerEvent))
            {
                await ProcessEventAsync(triggerEvent, workCancellationToken).ConfigureAwait(false);
                continue;
            }

            if (CommitPauseExitIfNeeded())
            {
                return;
            }

            using CancellationTokenSource wakeCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(workCancellationToken);
            Task<bool> wakeTask = _wakeChannel.Reader
                .WaitToReadAsync(wakeCancellation.Token)
                .AsTask();
            Task completed = await Task.WhenAny(tickTask, wakeTask).ConfigureAwait(false);
            if (ReferenceEquals(completed, tickTask))
            {
                wakeCancellation.Cancel();
                try
                {
                    await wakeTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (
                    wakeCancellation.IsCancellationRequested
                    && !ExceptionGraphClassifier.IsProcessFatal(exception))
                {
                    // The losing channel wait must not survive this loop iteration.
                }

                try
                {
                    await tickTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException exception) when (
                    !ExceptionGraphClassifier.IsProcessFatal(exception))
                {
                    if (workCancellationToken.IsCancellationRequested)
                    {
                        return;
                    }

                    tickTask = StartTickWait();
                    continue;
                }
                catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
                {
                    RecordFailure(SupervisorFailureClassifier.Classify(exception));
                    tickTask = StartTickWait();
                    continue;
                }

                if (IsAcceptingEvents)
                {
                    _pendingEvents.Enqueue(new TriggerSchedulerEvent(TriggerEventKind.Periodic));
                }

                tickTask = StartTickWait();
                continue;
            }

            try
            {
                if (!await wakeTask.ConfigureAwait(false))
                {
                    return;
                }
            }
            catch (OperationCanceledException exception) when (
                workCancellationToken.IsCancellationRequested
                && !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                return;
            }

            DrainWakeChannel();
        }
    }

    private async Task ProcessEventAsync(
        TriggerSchedulerEvent triggerEvent,
        CancellationToken cancellationToken)
    {
        try
        {
            bool hadPendingRelease = _pendingReleaseCandidates.Count > 0;
            bool releaseAcknowledgementsComplete = await AcknowledgePendingReleasesAsync(
                cancellationToken).ConfigureAwait(false);
            if (!_settings.IsEnabled)
            {
                if (hadPendingRelease && releaseAcknowledgementsComplete)
                {
                    RecordSuccess();
                }

                return;
            }

            TriggerSchedulerEvaluationOutcome outcome = await _evaluator.EvaluateAsync(
                triggerEvent,
                cancellationToken).ConfigureAwait(false);
            foreach (TriggerExecution execution in outcome.ReleaseCandidates)
            {
                _pendingReleaseCandidates[execution.ExecutionId] = execution;
            }

            releaseAcknowledgementsComplete &= await AcknowledgePendingReleasesAsync(
                cancellationToken).ConfigureAwait(false);
            if (outcome.DiagnosticCode is not null)
            {
                RecordFailure(outcome.DiagnosticCode);
            }
            else if (releaseAcknowledgementsComplete)
            {
                RecordSuccess();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            RecordFailure(SupervisorFailureClassifier.Classify(exception));
        }
    }

    private async Task<bool> AcknowledgePendingReleasesAsync(CancellationToken cancellationToken)
    {
        foreach (TriggerExecution execution in _pendingReleaseCandidates.Values.ToArray())
        {
            bool acknowledged = false;
            for (int attempt = 0; attempt < ImmediateReleaseAcknowledgementAttempts; attempt++)
            {
                try
                {
                    await _lifecycleHandoff.AcknowledgeReleasedExecutionAsync(
                        execution,
                        cancellationToken).ConfigureAwait(false);
                    acknowledged = true;
                    break;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
                {
                    RecordFailure(SupervisorFailureClassifier.Classify(exception));
                    if (attempt + 1 < ImmediateReleaseAcknowledgementAttempts)
                    {
                        await Task.Yield();
                    }
                }
            }

            if (acknowledged)
            {
                _pendingReleaseCandidates.Remove(execution.ExecutionId);
            }
        }

        return _pendingReleaseCandidates.Count == 0;
    }

    private bool CommitPauseExitIfNeeded()
    {
        lock (_publicationLock)
        {
            if (IsAcceptingEvents)
            {
                return false;
            }

            _pauseExitCommitted = true;
            return true;
        }
    }

    private Task StartTickWait()
    {
        CancellationToken token = _clockCancellation?.Token
            ?? throw new InvalidOperationException("The scheduler clock cancellation source is unavailable.");
        try
        {
            return _clock.WaitForNextTickAsync(token)
                ?? Task.FromException(
                    new InvalidOperationException("The scheduler clock returned a null wait task."));
        }
        catch (Exception exception)
        {
            return Task.FromException(exception);
        }
    }

    private void OnEventRaised(object? sender, TriggerSchedulerEvent triggerEvent)
    {
        lock (_publicationLock)
        {
            if (!IsAcceptingEvents)
            {
                return;
            }

            _pendingEvents.Enqueue(triggerEvent);
            SignalWork();
        }
    }

    private void Subscribe()
    {
        if (_subscribed)
        {
            return;
        }

        _eventSource.EventRaised += OnEventRaised;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
        {
            return;
        }

        _eventSource.EventRaised -= OnEventRaised;
        _subscribed = false;
    }

    private void SignalWork() => _wakeChannel.Writer.TryWrite(0);

    private void DrainWakeChannel()
    {
        while (_wakeChannel.Reader.TryRead(out _))
        {
        }
    }

    private void ReplaceClockCancellation()
    {
        CancellationTokenSource? previous = _clockCancellation;
        _clockCancellation = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();
    }

    private void CleanupCompletedRun()
    {
        if (_runTask is not null && !_runTask.IsCompleted)
        {
            return;
        }

        _runTask = null;
        _workCancellation?.Dispose();
        _workCancellation = null;
        _clockCancellation?.Dispose();
        _clockCancellation = null;
    }

    private void RecordFailure(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        SupervisorHealth previous = Health;
        DateTimeOffset failedAt = _clock.UtcNow;
        int failures = Math.Min(previous.ConsecutiveFailureCount + 1, int.MaxValue);
        Publish(new SupervisorHealth(
            failures >= 5 ? SupervisorHealthState.Degraded : SupervisorHealthState.Retrying,
            failures,
            0,
            previous.FirstFailureAt ?? failedAt,
            failedAt,
            null,
            diagnosticCode,
            previous.LastSuccessAt));
    }

    private void RecordSuccess()
    {
        SupervisorHealth previous = Health;
        DateTimeOffset succeededAt = _clock.UtcNow;
        if (previous.State is SupervisorHealthState.Retrying or SupervisorHealthState.Degraded)
        {
            Publish(previous with
            {
                State = SupervisorHealthState.Recovering,
                ConsecutiveFailureCount = 0,
                ConsecutiveSuccessCount = 1,
                LastSuccessAt = succeededAt,
            });
            return;
        }

        bool recovered = previous.State == SupervisorHealthState.Recovering
            && previous.ConsecutiveSuccessCount >= 1;
        int successes = Math.Min(previous.ConsecutiveSuccessCount + 1, int.MaxValue);
        Publish(previous with
        {
            State = recovered ? SupervisorHealthState.Healthy : previous.State,
            ConsecutiveFailureCount = 0,
            ConsecutiveSuccessCount = successes,
            FirstFailureAt = recovered ? null : previous.FirstFailureAt,
            LastFailureAt = recovered ? null : previous.LastFailureAt,
            ErrorCode = recovered ? null : previous.ErrorCode,
            LastSuccessAt = succeededAt,
        });
    }

    private void PublishStopped()
    {
        Publish(Health with
        {
            State = SupervisorHealthState.Stopped,
            NextAttemptAt = null,
        });
    }

    private void Publish(SupervisorHealth health)
    {
        Volatile.Write(ref _health, health);
        if (_healthChanged is null)
        {
            return;
        }

        try
        {
            _healthChanged(health);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            // Observers cannot own or terminate the scheduler.
        }
    }
}
