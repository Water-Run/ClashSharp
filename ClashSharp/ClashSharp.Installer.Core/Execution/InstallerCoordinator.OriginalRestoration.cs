using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Execution;

public sealed partial class InstallerCoordinator
{
    /// <summary>
    /// Preserves the original installation of an exact pending same-owner repair. The elevated
    /// participant owns recovery evidence, restoration, verification and public terminal writes.
    /// </summary>
    public async Task<InstallerExecutionResult> RestoreOriginalAsync(InstallerRequest request,
        IProgress<InstallerProgress>? progress, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);
        if (!_executionGate.Wait(0, CancellationToken.None))
        {
            return Result(InstallerExecutionOutcome.Blocked, "installer.concurrent_action_rejected", null, false);
        }
        InstallerTransactionSnapshot? durable = null;
        bool transactionStateReached = false;
        try
        {
            request.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            if (request.Operation != InstallerOperation.Repair || request.AllowReassociation)
            {
                throw new InstallerProtocolException("installer.recovery.operation_invalid");
            }
            InstallerEnvironmentSnapshot environment = await _environment.InspectAsync(request, cancellationToken).ConfigureAwait(false);
            InstallerExecutionResult? blocked = ValidateEnvironment(request, environment);
            if (blocked is not null) { return blocked; }
            await using IInstallerReleaseLease release = await _releaseVerifier.VerifyAsync(request, cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.release.lease_missing");
            ValidateRelease(request, release);
            durable = await _transactionReader.LoadAsync(cancellationToken).ConfigureAwait(false);
            transactionStateReached = true;
            if (durable is null) { throw new InstallerProtocolException("installer.recovery.transaction_missing"); }
            durable.Validate();
            if (!durable.Journal.Matches(request)) { throw new InstallerProtocolException("installer.transaction.release_conflict"); }
            return await CompleteOriginalRestorationAsync(request, release, durable, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (transactionStateReached) { durable = await RefreshDurableAsync(durable).ConfigureAwait(false); }
            return Result(InstallerExecutionOutcome.Cancelled, "installer.cancelled", durable?.Journal.Phase, durable is not null);
        }
        catch (InstallerUserCancelledException exception)
        {
            if (transactionStateReached) { durable = await RefreshDurableAsync(durable).ConfigureAwait(false); }
            return Result(InstallerExecutionOutcome.Cancelled, exception.DiagnosticCode, durable?.Journal.Phase, durable is not null);
        }
        catch (HelperStateReloadException exception)
        {
            durable = await RefreshDurableAsync(exception.FallbackState).ConfigureAwait(false);
            return Result(InstallerExecutionOutcome.Uncertain, exception.DiagnosticCode, durable?.Journal.Phase, durable is not null);
        }
        catch (InstallerStateUncertainException exception)
        {
            if (transactionStateReached) { durable = await RefreshDurableAsync(durable).ConfigureAwait(false); }
            return Result(InstallerExecutionOutcome.Uncertain, exception.DiagnosticCode, durable?.Journal.Phase, durable is not null);
        }
        catch (InstallerProtocolException exception)
        {
            if (transactionStateReached) { durable = await RefreshDurableAsync(durable).ConfigureAwait(false); }
            return Result(durable is null ? InstallerExecutionOutcome.Blocked : InstallerExecutionOutcome.Failed,
                exception.DiagnosticCode, durable?.Journal.Phase, durable is not null);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            if (transactionStateReached) { durable = await RefreshDurableAsync(durable).ConfigureAwait(false); }
            return Result(durable is null ? InstallerExecutionOutcome.Blocked : InstallerExecutionOutcome.Failed,
                "installer.unexpected_failure", durable?.Journal.Phase, durable is not null);
        }
        finally { _executionGate.Release(); }
    }

    private async Task<InstallerExecutionResult> CompleteOriginalRestorationAsync(InstallerRequest request,
        IInstallerReleaseLease release, InstallerTransactionSnapshot durable, IProgress<InstallerProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (request.Operation != InstallerOperation.Repair || request.AllowReassociation
            || durable.Journal.Phase is not (InstallerTransactionPhase.Prepared or InstallerTransactionPhase.MachineReserved
                or InstallerTransactionPhase.OriginalRestored))
        {
            throw new InstallerProtocolException("installer.recovery.phase_invalid");
        }
        if (_machineMutation is not IInstallerOriginalRestorationMutation restoration)
        {
            throw new InstallerProtocolException("installer.recovery.capability_unavailable");
        }
        ReportSafely(progress, durable.Journal.Phase, 20, "installer.progress.restoring_original");
        await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
        InstallerTransactionSnapshot restored = await restoration.RestoreOriginalAsync(request, release, durable, cancellationToken).ConfigureAwait(false);
        durable = ValidateHelperState(durable, restored, InstallerTransactionPhase.OriginalRestored);
        durable = await ConfirmHelperStateAsync(durable, cancellationToken).ConfigureAwait(false);
        ReportSafely(progress, durable.Journal.Phase, 90, "installer.progress.verifying_original");
        await ReverifyReleaseAsync(request, release, cancellationToken).ConfigureAwait(false);
        InstallerTransactionSnapshot receipt = await restoration.ClearOriginalRestoredAsync(request, release, durable, cancellationToken).ConfigureAwait(false);
        if (receipt != durable) { throw new InstallerProtocolException("installer.recovery.clear_receipt_mismatch"); }
        receipt.Validate();
        // Once the exact terminal clear is acknowledged, cancellation cannot replace observation
        // of its real outcome. Missing or unreadable state still fails the completion checks.
        await ConfirmHelperClearAsync(durable, CancellationToken.None).ConfigureAwait(false);
        ReportSafely(progress, InstallerTransactionPhase.OriginalRestored, 100, "installer.progress.original_restored");
        return Result(InstallerExecutionOutcome.Succeeded, "installer.recovery.original_restored", InstallerTransactionPhase.OriginalRestored, false);
    }
}
