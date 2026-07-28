using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;

namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Classifies whether the host may be disposed after a shutdown attempt.</summary>
public enum RuntimeShutdownOutcome
{
    /// <summary>All required shutdown preparation has committed and the outer lifetime may dispose the host.</summary>
    PreparedForHostDisposal,

    /// <summary>Shutdown did not cross its commit point and prior runtime state was restored.</summary>
    Aborted,

    /// <summary>Shutdown aborted and one or more participants could not restore their prior state.</summary>
    Degraded,
}

/// <summary>Returns the final verified lifecycle classification for one shutdown attempt.</summary>
/// <param name="Outcome">Whether the host is prepared, restored, or degraded.</param>
/// <param name="ErrorCode">Stable diagnostic code when shutdown was not clean.</param>
/// <param name="DegradedParticipants">Stable participant names that could not stop or restore.</param>
public sealed record RuntimeShutdownResult(
    RuntimeShutdownOutcome Outcome,
    string? ErrorCode,
    IReadOnlyList<string> DegradedParticipants);

/// <summary>Executes the shutdown network policy under an already-drained exclusive admission lease.</summary>
public interface IRuntimeShutdownNetworkCoordinator
{
    /// <summary>Applies and verifies the shutdown network intent without reacquiring mutation admission.</summary>
    Task<MutationResult<NetworkTransitionResult>> ApplyShutdownAsync(
        NetworkIntent intent,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken);
}

/// <summary>Thrown when host stop is requested before runtime shutdown can prepare host disposal.</summary>
public sealed class RuntimeShutdownNotPreparedException : InvalidOperationException
{
    /// <summary>Initializes the exception with the typed lifecycle result.</summary>
    public RuntimeShutdownNotPreparedException(RuntimeShutdownResult result)
        : base($"Runtime shutdown did not prepare host disposal; outcome '{result.Outcome}', code '{result.ErrorCode}'.")
    {
        Result = result;
    }

    /// <summary>Gets the typed failed shutdown result.</summary>
    public RuntimeShutdownResult Result { get; }
}

/// <summary>Coordinates admission drain, producer quiescence, network exit, and awaited runtime stop.</summary>
public sealed class RuntimeLifecycleCoordinator : IApplicationShutdownCoordinator
{
    /// <summary>Default upper bound for admission drain and producer quiescence.</summary>
    public static readonly TimeSpan DefaultQuiescenceTimeout = TimeSpan.FromSeconds(30);

    private readonly object _syncLock = new();
    private readonly MutationAdmissionBarrier _admissionBarrier;
    private readonly IRuntimeShutdownNetworkCoordinator _network;
    private readonly Func<NetworkIntent> _shutdownIntentFactory;
    private readonly IReadOnlyList<IRuntimeParticipant> _participants;
    private readonly TimeSpan _quiescenceTimeout;
    private Task<RuntimeShutdownResult>? _shutdownTask;
    private long _shutdownAttemptVersion;

    /// <summary>Initializes the sole host-owned runtime shutdown coordinator.</summary>
    public RuntimeLifecycleCoordinator(
        MutationAdmissionBarrier admissionBarrier,
        IRuntimeShutdownNetworkCoordinator network,
        Func<NetworkIntent> shutdownIntentFactory,
        IEnumerable<IRuntimeParticipant> participants,
        TimeSpan? quiescenceTimeout = null)
    {
        _admissionBarrier = admissionBarrier ?? throw new ArgumentNullException(nameof(admissionBarrier));
        _network = network ?? throw new ArgumentNullException(nameof(network));
        _shutdownIntentFactory = shutdownIntentFactory ?? throw new ArgumentNullException(nameof(shutdownIntentFactory));
        ArgumentNullException.ThrowIfNull(participants);
        _participants = participants.ToArray();
        if (_participants.Any(static participant => participant is null))
        {
            throw new ArgumentException("Runtime participants cannot contain null entries.", nameof(participants));
        }

        string? duplicateName = _participants
            .GroupBy(static participant => participant.Name, StringComparer.Ordinal)
            .FirstOrDefault(static group => string.IsNullOrWhiteSpace(group.Key) || group.Count() > 1)
            ?.Key;
        if (duplicateName is not null || _participants.Any(static participant => string.IsNullOrWhiteSpace(participant.Name)))
        {
            throw new ArgumentException("Runtime participant names must be non-empty and unique.", nameof(participants));
        }

        _quiescenceTimeout = quiescenceTimeout ?? DefaultQuiescenceTimeout;
        if (_quiescenceTimeout <= TimeSpan.Zero || _quiescenceTimeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(nameof(quiescenceTimeout));
        }
    }

    /// <summary>
    /// Shares one active shutdown preparation attempt, retaining successful completion and releasing failed attempts for retry.
    /// </summary>
    public Task<RuntimeShutdownResult> ShutdownAsync(CancellationToken cancellationToken)
    {
        lock (_syncLock)
        {
            if (_shutdownTask is null)
            {
                long attemptVersion = ++_shutdownAttemptVersion;
                _shutdownTask = RunShutdownAttemptAsync(attemptVersion, cancellationToken);
            }

            return _shutdownTask;
        }
    }

    /// <inheritdoc />
    async Task IApplicationShutdownCoordinator.StopAsync(CancellationToken cancellationToken)
    {
        RuntimeShutdownResult result = await ShutdownAsync(cancellationToken).ConfigureAwait(false);
        if (result.Outcome != RuntimeShutdownOutcome.PreparedForHostDisposal)
        {
            throw new RuntimeShutdownNotPreparedException(result);
        }
    }

    private async Task<RuntimeShutdownResult> RunShutdownAttemptAsync(
        long attemptVersion,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        RuntimeShutdownResult result;
        try
        {
            result = await ShutdownCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            ReleaseRetryableAttempt(attemptVersion);
            throw;
        }

        if (result.Outcome != RuntimeShutdownOutcome.PreparedForHostDisposal)
        {
            ReleaseRetryableAttempt(attemptVersion);
        }

        return result;
    }

    private void ReleaseRetryableAttempt(long attemptVersion)
    {
        lock (_syncLock)
        {
            if (_shutdownAttemptVersion == attemptVersion)
            {
                _shutdownTask = null;
            }
        }
    }

    private async Task<RuntimeShutdownResult> ShutdownCoreAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource transitionDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        transitionDeadline.CancelAfter(_quiescenceTimeout);
        while (true)
        {
            switch (_admissionBarrier.State)
            {
                case MutationAdmissionState.Open:
                    RuntimeShutdownResult result = await ShutdownOpenStateAsync(cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(result.ErrorCode, "mutation-admission-busy", StringComparison.Ordinal))
                    {
                        return result;
                    }

                    break;
                case MutationAdmissionState.Closing:
                    break;
                case MutationAdmissionState.RecoveryOnly:
                case MutationAdmissionState.RecoveryClosing:
                    return await ShutdownRecoveryStateAsync(cancellationToken).ConfigureAwait(false);
                case MutationAdmissionState.ClosedForShutdown:
                    return await ShutdownClosedStateAsync().ConfigureAwait(false);
                default:
                    throw new InvalidOperationException("The mutation admission state is unsupported.");
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), transitionDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                return CreateResult(
                    RuntimeShutdownOutcome.Aborted,
                    cancellationToken.IsCancellationRequested
                        ? "shutdown-cancelled"
                        : "mutation-admission-timeout");
            }
        }
    }

    private async Task<RuntimeShutdownResult> ShutdownOpenStateAsync(CancellationToken callerToken)
    {
        using CancellationTokenSource quiescenceDeadline = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        quiescenceDeadline.CancelAfter(_quiescenceTimeout);
        MutationAdmissionLease admissionLease;
        try
        {
            admissionLease = await _admissionBarrier
                .CloseAndDrainAsync(MutationAdmissionClosure.Destructive, quiescenceDeadline.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            string errorCode = callerToken.IsCancellationRequested
                ? "shutdown-cancelled"
                : "quiescence-timeout";
            return CreateResult(RuntimeShutdownOutcome.Aborted, errorCode);
        }
        catch (MutationAdmissionRejectedException)
        {
            return CreateResult(RuntimeShutdownOutcome.Aborted, "mutation-admission-busy");
        }

        await using (admissionLease.ConfigureAwait(false))
        {
            QuiescenceSession session = new();
            string? quiescenceError = null;
            try
            {
                await session.QuiesceAsync(_participants, quiescenceDeadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                quiescenceError = callerToken.IsCancellationRequested
                    ? "shutdown-cancelled"
                    : "quiescence-timeout";
            }
            catch (Exception exception) when (ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                await RestoreAfterFatalFailureAsync(session, exception).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                quiescenceError = "quiescence-failed";
            }

            if (quiescenceError is not null)
            {
                return await RestoreAfterAbortAsync(session, quiescenceError).ConfigureAwait(false);
            }

            MutationResult<NetworkTransitionResult> networkResult;
            try
            {
                NetworkIntent intent = _shutdownIntentFactory();
                networkResult = await _network
                    .ApplyShutdownAsync(intent, admissionLease, callerToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                !ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                return await RestoreAfterAbortAsync(session, "shutdown-network-cancelled").ConfigureAwait(false);
            }
            catch (Exception exception) when (ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                await RestoreAfterFatalFailureAsync(session, exception).ConfigureAwait(false);
                throw;
            }
            catch (Exception)
            {
                return await RestoreAfterAbortAsync(session, "shutdown-network-failed").ConfigureAwait(false);
            }

            bool networkTargetCommitted = networkResult.Outcome is
                MutationOutcome.Succeeded or MutationOutcome.CommittedRecoveryRequired;
            if (!networkTargetCommitted)
            {
                return await RestoreAfterAbortAsync(
                    session,
                    networkResult.ErrorCode ?? "shutdown-network-failed").ConfigureAwait(false);
            }

            admissionLease.CommitShutdown();
            IReadOnlyList<string> stopFailures = await StopWithRecoveryDeadlineAsync().ConfigureAwait(false);
            bool cleanCommit = networkResult.Outcome == MutationOutcome.Succeeded && stopFailures.Count == 0;
            return cleanCommit
                ? CreateResult(RuntimeShutdownOutcome.PreparedForHostDisposal, null)
                : CreateResult(
                    RuntimeShutdownOutcome.PreparedForHostDisposal,
                    networkResult.ErrorCode ?? "runtime-stop-degraded",
                    stopFailures);
        }
    }

    private async Task<RuntimeShutdownResult> ShutdownRecoveryStateAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _admissionBarrier.RequestRecoveryShutdownAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return CreateResult(RuntimeShutdownOutcome.Aborted, "recovery-shutdown-cancelled");
        }

        return await ShutdownClosedStateAsync().ConfigureAwait(false);
    }

    private async Task<RuntimeShutdownResult> ShutdownClosedStateAsync()
    {
        QuiescenceSession session = new();
        List<string> degraded = [];
        using (CancellationTokenSource deadline = new(_quiescenceTimeout))
        {
            try
            {
                await session.QuiesceAsync(_participants, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                degraded.Add("runtime-quiescence");
            }
        }

        IReadOnlyList<string> stopFailures = await StopWithRecoveryDeadlineAsync().ConfigureAwait(false);
        degraded.AddRange(stopFailures);
        return degraded.Count == 0
            ? CreateResult(RuntimeShutdownOutcome.PreparedForHostDisposal, null)
            : CreateResult(
                RuntimeShutdownOutcome.PreparedForHostDisposal,
                "recovery-runtime-stop-degraded",
                degraded);
    }

    private async Task<RuntimeShutdownResult> RestoreAfterAbortAsync(
        QuiescenceSession session,
        string originalErrorCode)
    {
        IReadOnlyList<string> restoreFailures;
        using (CancellationTokenSource recoveryDeadline = new(_quiescenceTimeout))
        {
            restoreFailures = await session.ResumeAsync(recoveryDeadline.Token).ConfigureAwait(false);
        }

        return restoreFailures.Count == 0
            ? CreateResult(RuntimeShutdownOutcome.Aborted, originalErrorCode)
            : CreateResult(
                RuntimeShutdownOutcome.Degraded,
                "quiescence-restore-failed",
                restoreFailures);
    }

    private async Task RestoreAfterFatalFailureAsync(
        QuiescenceSession session,
        Exception processFatalFailure)
    {
        try
        {
            _ = await RestoreAfterAbortAsync(session, "process-fatal-failure").ConfigureAwait(false);
        }
        catch (Exception restoreFailure)
        {
            throw new AggregateException(processFatalFailure, restoreFailure);
        }

        ExceptionDispatchInfo.Capture(processFatalFailure).Throw();
    }

    private async Task<IReadOnlyList<string>> StopWithRecoveryDeadlineAsync()
    {
        using CancellationTokenSource recoveryDeadline = new(_quiescenceTimeout);
        List<string> failures = [];
        List<Exception> processFatalFailures = [];
        for (int index = _participants.Count - 1; index >= 0; index--)
        {
            IRuntimeParticipant participant = _participants[index];
            try
            {
                await participant.StopAsync(recoveryDeadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                processFatalFailures.Add(exception);
            }
            catch (Exception)
            {
                failures.Add(participant.Name);
            }
        }

        if (processFatalFailures.Count != 0)
        {
            throw new AggregateException(
                "One or more runtime participants failed fatally while stopping.",
                processFatalFailures);
        }

        return failures;
    }

    private static RuntimeShutdownResult CreateResult(
        RuntimeShutdownOutcome outcome,
        string? errorCode,
        IReadOnlyList<string>? degradedParticipants = null)
    {
        return new RuntimeShutdownResult(
            outcome,
            errorCode,
            degradedParticipants?.ToArray() ?? []);
    }
}
