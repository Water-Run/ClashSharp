using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Machines;

/// <summary>Describes whether a helper command needs mutation or only committed-state replay verification.</summary>
public enum InstallerMachineHelperSessionDisposition
{
    /// <summary>Execute the requested idempotent privileged operation.</summary>
    Execute,

    /// <summary>The protected store already contains the expected result; verify and acknowledge it.</summary>
    VerifyCommittedReplay,
}

/// <summary>
/// Binds one persistent elevated-helper stream to its bootstrap, protected store, and terminal results.
/// </summary>
public sealed class InstallerMachineHelperSessionGuard
{
    private readonly object _gate = new();
    private readonly InstallerMachineHelperInvocation _bootstrap;
    private InstallerTransactionSnapshot? _latestProtectedState;
    private PendingCommand? _pending;
    private bool _bootstrapConsumed;

    /// <summary>
    /// Creates a guard from the exact runas bootstrap and the helper-read protected state.
    /// </summary>
    public InstallerMachineHelperSessionGuard(
        InstallerMachineHelperInvocation bootstrap,
        InstallerTransactionSnapshot? protectedState)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        bootstrap.Validate();
        protectedState?.Validate();
        if (protectedState is not null
            && !string.Equals(
                protectedState.Journal.TransactionId,
                bootstrap.TransactionId,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.session_transaction_mismatch");
        }

        _bootstrap = bootstrap;
        _latestProtectedState = protectedState;
    }

    /// <summary>
    /// Begins one command only when its journal is the protected state or its idempotent predecessor.
    /// </summary>
    public InstallerMachineHelperSessionDisposition Begin(
        InstallerMachineHelperCommand command,
        InstallerTransactionSnapshot? protectedState)
    {
        ArgumentNullException.ThrowIfNull(command);
        InstallerTransactionSnapshot requestState = command.ToDurableState();
        InstallerMachineHelperInvocation invocation = command.ToInvocation();
        protectedState?.Validate();

        lock (_gate)
        {
            if (_pending is not null)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_command_pending");
            }

            bool firstCommand = !_bootstrapConsumed;
            if (firstCommand && invocation != _bootstrap)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_bootstrap_mismatch");
            }

            if (!string.Equals(
                    requestState.Journal.TransactionId,
                    _bootstrap.TransactionId,
                    StringComparison.Ordinal)
                || protectedState is not null
                    && !string.Equals(
                        protectedState.Journal.TransactionId,
                        _bootstrap.TransactionId,
                        StringComparison.Ordinal))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_transaction_mismatch");
            }

            if (firstCommand)
            {
                if (protectedState != _latestProtectedState)
                {
                    throw new InstallerProtocolException(
                        "installer.machine_helper.session_protected_state_changed");
                }
            }
            else
            {
                ValidateProtectedProgress(_latestProtectedState, protectedState);
            }

            InstallerTransactionSnapshot expectedResult =
                command.GetExpectedSuccessfulState();
            InstallerMachineHelperSessionDisposition disposition;
            if (protectedState is null)
            {
                if (_latestProtectedState is not null
                    || command.Verb != InstallerMachineHelperVerb.Prepare
                    || requestState.Journal.Phase != InstallerTransactionPhase.Prepared)
                {
                    throw new InstallerProtocolException(
                        "installer.machine_helper.session_protected_state_missing");
                }

                disposition = InstallerMachineHelperSessionDisposition.Execute;
            }
            else
            {
                if (!HasSameImmutableIdentity(
                        requestState.Journal,
                        protectedState.Journal))
                {
                    throw new InstallerProtocolException(
                        "installer.machine_helper.session_identity_mismatch");
                }

                bool exactRequest = requestState == protectedState;
                bool committedReplay = expectedResult == protectedState;
                if (!exactRequest && !committedReplay)
                {
                    if (requestState.Journal.Generation < protectedState.Journal.Generation)
                    {
                        throw new InstallerProtocolException(
                            "installer.machine_helper.session_journal_regressed");
                    }

                    throw new InstallerProtocolException(
                        "installer.machine_helper.session_protected_state_mismatch");
                }

                disposition = committedReplay
                    ? InstallerMachineHelperSessionDisposition.VerifyCommittedReplay
                    : InstallerMachineHelperSessionDisposition.Execute;
            }

            _bootstrapConsumed = true;
            _latestProtectedState = protectedState;
            _pending = new PendingCommand(command, protectedState, disposition);
            return disposition;
        }
    }

    /// <summary>
    /// Commits a terminal result only after an authoritative store reload proves its journal.
    /// </summary>
    public InstallerTransactionSnapshot Complete(
        InstallerMachineHelperResult result,
        InstallerTransactionSnapshot? protectedState)
    {
        ArgumentNullException.ThrowIfNull(result);
        protectedState?.Validate();

        lock (_gate)
        {
            PendingCommand pending = _pending
                ?? throw new InstallerProtocolException(
                    "installer.machine_helper.session_completion_missing");
            InstallerTransactionSnapshot resultState =
                result.ValidateAgainst(pending.Command);
            if (protectedState is not null
                && !HasSameImmutableIdentity(
                    pending.Command.ToDurableState().Journal,
                    protectedState.Journal))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_identity_mismatch");
            }

            bool protectedStateMatches = result.Outcome switch
            {
                InstallerMachineHelperOutcome.Succeeded =>
                    protectedState == resultState,
                InstallerMachineHelperOutcome.Failed
                    when pending.Disposition
                        != InstallerMachineHelperSessionDisposition.VerifyCommittedReplay =>
                    pending.ProtectedStateBefore is null
                        ? protectedState is null
                            || protectedState == pending.Command.ToDurableState()
                        : protectedState == pending.ProtectedStateBefore,
                InstallerMachineHelperOutcome.PostconditionFailed
                    when pending.Disposition
                        == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay =>
                    protectedState == resultState,
                _ => false,
            };
            if (!protectedStateMatches)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_protected_state_mismatch");
            }

            _latestProtectedState = protectedState;
            _pending = null;
            return resultState;
        }
    }

    /// <summary>
    /// Clears an interrupted command only after reloading an allowed pre- or post-command state.
    /// </summary>
    public InstallerTransactionSnapshot? ReconcileAfterAbort(
        InstallerTransactionSnapshot? protectedState)
    {
        protectedState?.Validate();
        lock (_gate)
        {
            PendingCommand pending = _pending
                ?? throw new InstallerProtocolException(
                    "installer.machine_helper.session_completion_missing");
            InstallerTransactionSnapshot requestState = pending.Command.ToDurableState();
            InstallerTransactionSnapshot expectedResult =
                pending.Command.GetExpectedSuccessfulState();
            if (protectedState is not null
                && !HasSameImmutableIdentity(
                    requestState.Journal,
                    protectedState.Journal))
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_identity_mismatch");
            }

            bool allowed = pending.ProtectedStateBefore is null
                ? protectedState is null
                    || protectedState == requestState
                    || protectedState == expectedResult
                : protectedState == pending.ProtectedStateBefore
                    || protectedState == expectedResult;
            if (!allowed)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_abort_state_invalid");
            }

            _latestProtectedState = protectedState;
            _pending = null;
            return protectedState;
        }
    }

    private static void ValidateProtectedProgress(
        InstallerTransactionSnapshot? latest,
        InstallerTransactionSnapshot? observed)
    {
        if (latest is null)
        {
            if (observed is not null)
            {
                throw new InstallerProtocolException(
                    "installer.machine_helper.session_protected_state_changed");
            }

            return;
        }

        if (observed is null)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.session_protected_state_missing");
        }

        if (!string.Equals(
                latest.Journal.TransactionId,
                observed.Journal.TransactionId,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.session_transaction_mismatch");
        }

        if (!HasSameImmutableIdentity(latest.Journal, observed.Journal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.session_identity_mismatch");
        }

        if (observed == latest)
        {
            return;
        }

        if (observed.Journal.Generation < latest.Journal.Generation)
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.session_journal_regressed");
        }

        throw new InstallerProtocolException(
            "installer.machine_helper.session_protected_state_changed");
    }

    private static bool HasSameImmutableIdentity(
        InstallerTransactionJournal first,
        InstallerTransactionJournal second) =>
        first.Schema == second.Schema
        && string.Equals(first.TransactionId, second.TransactionId, StringComparison.Ordinal)
        && first.Operation == second.Operation
        && string.Equals(first.TargetSid, second.TargetSid, StringComparison.Ordinal)
        && first.AllowReassociation == second.AllowReassociation
        && string.Equals(
            first.ExpectedPackageVersion,
            second.ExpectedPackageVersion,
            StringComparison.Ordinal)
        && string.Equals(
            first.InstallerPayloadSha256,
            second.InstallerPayloadSha256,
            StringComparison.Ordinal);

    private sealed record PendingCommand(
        InstallerMachineHelperCommand Command,
        InstallerTransactionSnapshot? ProtectedStateBefore,
        InstallerMachineHelperSessionDisposition Disposition);
}
