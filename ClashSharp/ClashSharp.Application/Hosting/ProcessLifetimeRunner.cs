using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Lifecycle;

namespace ClashSharp.ApplicationModel.Hosting;

/// <summary>Owns host stop and disposal outside the dependency-injection provider.</summary>
public sealed class ProcessLifetimeRunner
{
    private readonly object _syncLock = new();
    private IApplicationHost? _host;
    private Task? _stopTask;
    private DurableRequestCheckpoint? _durableRequestCheckpoint;
    private AttachedHostState _attachedHostState;
    private long _stopAttemptVersion;

    /// <summary>Gets whether host ownership is currently attached to this process lifetime.</summary>
    public bool HasAttachedHost
    {
        get
        {
            lock (_syncLock)
            {
                return _host is not null;
            }
        }
    }

    /// <summary>
    /// Gets whether the attached host has not completed a successful stop and may resume application work.
    /// </summary>
    /// <remarks>
    /// Host ownership can remain attached after a successful stop while durable shutdown outcome
    /// persistence is retried. Such a stopped host must be disposed or followed by process exit;
    /// it cannot safely resume startup or other application work.
    /// </remarks>
    public bool CanResumeAttachedHost
    {
        get
        {
            lock (_syncLock)
            {
                return _host is not null
                    && _attachedHostState == AttachedHostState.Running;
            }
        }
    }

    /// <summary>Attaches the one host owned by this process lifetime.</summary>
    /// <param name="host">Started primary application host.</param>
    public void AttachHost(IApplicationHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        lock (_syncLock)
        {
            if (_host is not null || _stopTask is not null)
            {
                throw new InvalidOperationException("A host is already attached or process shutdown has started.");
            }

            _host = host;
            _attachedHostState = AttachedHostState.Running;
        }
    }

    /// <summary>Stops and disposes the attached host once.</summary>
    /// <param name="cancellationToken">Bounds host shutdown.</param>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            if (_host is null)
            {
                return Task.CompletedTask;
            }

            IApplicationHost host = _host;
            StopAttempt attempt = new(++_stopAttemptVersion);
            _stopTask = RunRetryableStopAttemptAsync(
                attempt,
                () => StopAttachedAndDisposeAsync(host, attempt, cancellationToken));
            return _stopTask;
        }
    }

    /// <summary>Processes one accepted lifetime request after its producer releases all owned work.</summary>
    public Task ProcessAsync(
        ApplicationLifetimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_syncLock)
        {
            if (_stopTask is not null)
            {
                return _stopTask;
            }

            if (request.TerminalStatePersistence
                == ApplicationLifetimeTerminalStatePersistence.Confirmed)
            {
                return Task.FromException(new InvalidOperationException(
                    "A durable lifetime request with a confirmed terminal state cannot be processed again."));
            }

            if (_host is null)
            {
                return request.Handoff is null
                    ? Task.CompletedTask
                    : Task.FromException(new InvalidOperationException(
                        "A durable lifetime handoff cannot be processed without an attached host."));
            }

            IApplicationHost host = _host;
            DurableRequestCheckpoint? durableCheckpoint = request.Handoff is null
                ? null
                : GetOrCreateDurableCheckpoint(request);
            StopAttempt attempt = new(++_stopAttemptVersion);
            _stopTask = RunRetryableStopAttemptAsync(
                attempt,
                () => ProcessAttachedRequestAsync(
                    host,
                    request,
                    durableCheckpoint,
                    attempt,
                    cancellationToken));
            return _stopTask;
        }
    }

    private DurableRequestCheckpoint GetOrCreateDurableCheckpoint(
        ApplicationLifetimeRequest request)
    {
        IApplicationLifetimeHandoff handoff = request.Handoff
            ?? throw new ArgumentException("A durable checkpoint requires a handoff.", nameof(request));
        if (_durableRequestCheckpoint is null)
        {
            _durableRequestCheckpoint = new DurableRequestCheckpoint(handoff.IdempotencyKey);
        }
        else if (!StringComparer.Ordinal.Equals(
            _durableRequestCheckpoint.IdempotencyKey,
            handoff.IdempotencyKey))
        {
            if (_durableRequestCheckpoint.Phase == DurableRequestPhase.TerminalFailure)
            {
                _durableRequestCheckpoint = new DurableRequestCheckpoint(handoff.IdempotencyKey);
            }
            else
            {
                throw new InvalidOperationException(
                    "A different durable lifetime request already owns the host shutdown checkpoint.");
            }
        }

        return _durableRequestCheckpoint;
    }

    private async Task RunRetryableStopAttemptAsync(
        StopAttempt attempt,
        Func<Task> stopOperation)
    {
        await Task.Yield();
        try
        {
            await stopOperation().ConfigureAwait(false);
        }
        catch
        {
            if (attempt.Phase == HostShutdownPhase.PreDisposal)
            {
                lock (_syncLock)
                {
                    if (_stopAttemptVersion == attempt.Version)
                    {
                        _stopTask = null;
                    }
                }
            }

            throw;
        }
    }

    private async Task ProcessAttachedRequestAsync(
        IApplicationHost host,
        ApplicationLifetimeRequest request,
        DurableRequestCheckpoint? durableCheckpoint,
        StopAttempt attempt,
        CancellationToken cancellationToken)
    {
        IApplicationLifetimeHandoff? handoff = request.Handoff;
        if (handoff is null)
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
            MarkAttachedHostStopped(host);
            attempt.BeginDisposal();
            await DisposeAndReleaseOwnershipAsync(host, attempt).ConfigureAwait(false);
            return;
        }

        DurableRequestCheckpoint checkpoint = durableCheckpoint
            ?? throw new InvalidOperationException("The durable shutdown checkpoint is unavailable.");
        if (checkpoint.Phase == DurableRequestPhase.WaitingForRelease)
        {
            await handoff.WaitForReleaseAsync(cancellationToken).ConfigureAwait(false);
            checkpoint.AdvanceTo(DurableRequestPhase.RecordingShutdownStart);
        }

        if (checkpoint.Phase == DurableRequestPhase.RecordingShutdownStart)
        {
            await handoff.MarkShutdownStartedAsync(cancellationToken).ConfigureAwait(false);
            checkpoint.AdvanceTo(DurableRequestPhase.StoppingHost);
        }

        if (checkpoint.Phase == DurableRequestPhase.StoppingHost)
        {
            try
            {
                await host.StopAsync(cancellationToken).ConfigureAwait(false);
                MarkAttachedHostStopped(host);
                checkpoint.AdvanceTo(DurableRequestPhase.RecordingShutdownSuccess);
            }
            catch (Exception stopException)
            {
                (ApplicationLifetimeShutdownFailureKind failureKind, string diagnosticCode) =
                    ClassifyShutdownFailure(stopException);
                checkpoint.RecordStopFailure(stopException, failureKind, diagnosticCode);
            }
        }

        if (checkpoint.Phase == DurableRequestPhase.RecordingShutdownFailure)
        {
            try
            {
                await handoff.MarkShutdownFailedAsync(
                    checkpoint.FailureKind,
                    checkpoint.DiagnosticCode,
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception handoffException)
            {
                throw new AggregateException(
                    checkpoint.StopException.SourceException,
                    handoffException);
            }

            request.ConfirmTerminalStatePersistence();
            checkpoint.AdvanceTo(DurableRequestPhase.TerminalFailure);
            checkpoint.StopException.Throw();
        }

        if (checkpoint.Phase == DurableRequestPhase.RecordingShutdownSuccess)
        {
            await handoff.MarkShutdownSucceededAsync(CancellationToken.None).ConfigureAwait(false);
            request.ConfirmTerminalStatePersistence();
            checkpoint.AdvanceTo(DurableRequestPhase.DisposingHost);
        }

        if (checkpoint.Phase != DurableRequestPhase.DisposingHost)
        {
            throw new InvalidOperationException(
                $"The durable lifetime request cannot continue from phase '{checkpoint.Phase}'.");
        }

        attempt.BeginDisposal();
        await DisposeAndReleaseOwnershipAsync(host, attempt).ConfigureAwait(false);
        checkpoint.AdvanceTo(DurableRequestPhase.Completed);
    }

    private static (ApplicationLifetimeShutdownFailureKind Kind, string DiagnosticCode)
        ClassifyShutdownFailure(Exception exception)
    {
        return exception switch
        {
            RuntimeShutdownNotPreparedException notPrepared => (
                ApplicationLifetimeShutdownFailureKind.Failed,
                notPrepared.Result.ErrorCode ?? "trigger.handoff.shutdown_not_prepared"),
            OperationCanceledException => (
                ApplicationLifetimeShutdownFailureKind.Uncertain,
                "trigger.handoff.shutdown_cancelled"),
            _ => (
                ApplicationLifetimeShutdownFailureKind.Uncertain,
                "trigger.handoff.shutdown_unexpected"),
        };
    }

    private async Task StopAttachedAndDisposeAsync(
        IApplicationHost host,
        StopAttempt attempt,
        CancellationToken cancellationToken)
    {
        await host.StopAsync(cancellationToken).ConfigureAwait(false);
        MarkAttachedHostStopped(host);
        attempt.BeginDisposal();
        await DisposeAndReleaseOwnershipAsync(host, attempt).ConfigureAwait(false);
    }

    private void MarkAttachedHostStopped(IApplicationHost host)
    {
        lock (_syncLock)
        {
            if (ReferenceEquals(_host, host))
            {
                _attachedHostState = AttachedHostState.Stopped;
            }
        }
    }

    private async Task DisposeAndReleaseOwnershipAsync(
        IApplicationHost host,
        StopAttempt attempt)
    {
        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            throw new ApplicationHostDisposalException(exception);
        }
        finally
        {
            lock (_syncLock)
            {
                if (ReferenceEquals(_host, host))
                {
                    _host = null;
                    _attachedHostState = AttachedHostState.None;
                }
            }

            attempt.Complete();
        }
    }

    internal static async Task StopAndDisposeAsync(IApplicationHost host, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(host);
        Exception? stopException = null;
        try
        {
            await host.StopAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            stopException = exception;
        }

        try
        {
            await host.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception disposeException) when (stopException is not null)
        {
            throw new AggregateException(stopException, disposeException);
        }

        if (stopException is not null)
        {
            ExceptionDispatchInfo.Capture(stopException).Throw();
        }
    }

    private enum HostShutdownPhase
    {
        PreDisposal,
        Disposing,
        Completed,
    }

    private enum AttachedHostState
    {
        None,
        Running,
        Stopped,
    }

    private enum DurableRequestPhase
    {
        WaitingForRelease,
        RecordingShutdownStart,
        StoppingHost,
        RecordingShutdownSuccess,
        RecordingShutdownFailure,
        TerminalFailure,
        DisposingHost,
        Completed,
    }

    private sealed class DurableRequestCheckpoint(string idempotencyKey)
    {
        public string IdempotencyKey { get; } = idempotencyKey;

        public DurableRequestPhase Phase { get; private set; }

        public ExceptionDispatchInfo StopException { get; private set; } = null!;

        public ApplicationLifetimeShutdownFailureKind FailureKind { get; private set; }

        public string DiagnosticCode { get; private set; } = string.Empty;

        public void AdvanceTo(DurableRequestPhase phase)
        {
            Phase = phase;
        }

        public void RecordStopFailure(
            Exception exception,
            ApplicationLifetimeShutdownFailureKind failureKind,
            string diagnosticCode)
        {
            StopException = ExceptionDispatchInfo.Capture(exception);
            FailureKind = failureKind;
            DiagnosticCode = diagnosticCode;
            Phase = DurableRequestPhase.RecordingShutdownFailure;
        }
    }

    private sealed class StopAttempt(long version)
    {
        private int _phase;

        public long Version { get; } = version;

        public HostShutdownPhase Phase => (HostShutdownPhase)Volatile.Read(ref _phase);

        public void BeginDisposal()
        {
            Interlocked.Exchange(ref _phase, (int)HostShutdownPhase.Disposing);
        }

        public void Complete()
        {
            Interlocked.Exchange(ref _phase, (int)HostShutdownPhase.Completed);
        }
    }
}
