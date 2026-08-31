using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Machines;

/// <summary>
/// Owns every protected-journal mutation for one authenticated elevated-helper session.
/// </summary>
public sealed class InstallerMachineHelperAuthoritySession
{
    private readonly InstallerMachineHelperSessionGuard _guard;
    private readonly IInstallerMachineHelperOperationExecutor _operations;
    private readonly IInstallerTransactionStore _transactionStore;
    private readonly string? _expectedTargetSid;

    private InstallerMachineHelperAuthoritySession(
        InstallerMachineHelperInvocation bootstrap,
        InstallerTransactionSnapshot? protectedState,
        IInstallerTransactionStore transactionStore,
        IInstallerMachineHelperOperationExecutor operations,
        string? expectedTargetSid)
    {
        _guard = new InstallerMachineHelperSessionGuard(bootstrap, protectedState);
        _transactionStore = transactionStore;
        _operations = operations;
        _expectedTargetSid = expectedTargetSid;
    }

    /// <summary>Creates a session from the helper's own authoritative protected-store read.</summary>
    public static async Task<InstallerMachineHelperAuthoritySession> CreateAsync(
        InstallerMachineHelperInvocation bootstrap,
        IInstallerTransactionStore transactionStore,
        IInstallerMachineHelperOperationExecutor operations,
        CancellationToken cancellationToken)
        => await CreateCoreAsync(
                bootstrap,
                transactionStore,
                operations,
                expectedTargetSid: null,
                cancellationToken)
            .ConfigureAwait(false);

    /// <summary>
    /// Creates a session additionally bound to the authenticated unelevated parent's exact user SID.
    /// </summary>
    public static async Task<InstallerMachineHelperAuthoritySession> CreateAsync(
        InstallerMachineHelperInvocation bootstrap,
        string expectedTargetSid,
        IInstallerTransactionStore transactionStore,
        IInstallerMachineHelperOperationExecutor operations,
        CancellationToken cancellationToken)
    {
        InstallerProtocolValidation.ValidateTargetSid(expectedTargetSid);
        return await CreateCoreAsync(
                bootstrap,
                transactionStore,
                operations,
                expectedTargetSid,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<InstallerMachineHelperAuthoritySession> CreateCoreAsync(
        InstallerMachineHelperInvocation bootstrap,
        IInstallerTransactionStore transactionStore,
        IInstallerMachineHelperOperationExecutor operations,
        string? expectedTargetSid,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bootstrap);
        ArgumentNullException.ThrowIfNull(transactionStore);
        ArgumentNullException.ThrowIfNull(operations);
        bootstrap.Validate();
        InstallerTransactionSnapshot? protectedState = await transactionStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        protectedState?.Validate();
        return new(
            bootstrap,
            protectedState,
            transactionStore,
            operations,
            expectedTargetSid);
    }

    /// <summary>
    /// Executes one journal-bound command and returns only after authoritative state reconciliation.
    /// </summary>
    public async Task<InstallerMachineHelperResult> ExecuteAsync(
        InstallerMachineHelperCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        InstallerTransactionSnapshot requestState = command.ToDurableState();
        if (_expectedTargetSid is not null
            && !string.Equals(
                requestState.Journal.TargetSid,
                _expectedTargetSid,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine_helper.target_sid_mismatch");
        }

        InstallerTransactionSnapshot? protectedBefore = await _transactionStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        protectedBefore?.Validate();
        InstallerMachineHelperSessionDisposition disposition = _guard.Begin(
            command,
            protectedBefore);

        try
        {
            if (disposition == InstallerMachineHelperSessionDisposition.Execute
                && protectedBefore is null)
            {
                InstallerTransactionSnapshot persistedIntent = await _transactionStore
                    .SaveAsync(
                        requestState.Journal,
                        expectedCurrentHash: null,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireExactState(
                    requestState,
                    persistedIntent,
                    "installer.machine_helper.prepared_commit_mismatch");
                protectedBefore = persistedIntent;
            }

            try
            {
                await _operations
                    .ExecuteAsync(command, disposition, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (InstallerProtocolException exception)
            {
                return await CompleteStableFailureAsync(
                        command,
                        disposition,
                        exception.DiagnosticCode,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            InstallerMachineHelperResult result;
            if (disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay)
            {
                result = InstallerMachineHelperResult.Succeeded(
                    command,
                    command.GetExpectedSuccessfulState());
            }
            else if (command.Verb == InstallerMachineHelperVerb.Clear)
            {
                await _transactionStore
                    .ClearVerifiedAsync(
                        requestState.Journal.TransactionId,
                        requestState.ContentHash,
                        cancellationToken)
                    .ConfigureAwait(false);
                result = InstallerMachineHelperResult.Succeeded(command, requestState);
            }
            else
            {
                InstallerTransactionSnapshot expected = command.GetExpectedSuccessfulState();
                InstallerTransactionSnapshot committed = await _transactionStore
                    .SaveAsync(
                        expected.Journal,
                        requestState.ContentHash,
                        cancellationToken)
                    .ConfigureAwait(false);
                RequireExactState(
                    expected,
                    committed,
                    "installer.machine_helper.result_commit_mismatch");
                result = InstallerMachineHelperResult.Succeeded(command, committed);
            }

            InstallerTransactionSnapshot? protectedAfter = await _transactionStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            protectedAfter?.Validate();
            _ = _guard.Complete(result, protectedAfter);
            return result;
        }
        catch
        {
            await ReconcileAfterAbortAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<InstallerMachineHelperResult> CompleteStableFailureAsync(
        InstallerMachineHelperCommand command,
        InstallerMachineHelperSessionDisposition disposition,
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        InstallerMachineHelperResult result = disposition
            == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay
            ? InstallerMachineHelperResult.PostconditionFailed(
                command,
                command.GetExpectedSuccessfulState(),
                diagnosticCode)
            : InstallerMachineHelperResult.Failed(command, diagnosticCode);
        InstallerTransactionSnapshot? protectedAfter = await _transactionStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        protectedAfter?.Validate();
        _ = _guard.Complete(result, protectedAfter);
        return result;
    }

    private async Task ReconcileAfterAbortAsync()
    {
        try
        {
            InstallerTransactionSnapshot? protectedState = await _transactionStore
                .LoadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            protectedState?.Validate();
            _ = _guard.ReconcileAfterAbort(protectedState);
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerStateUncertainException(
                "installer.machine_helper.reconciliation_failed");
        }
    }

    private static void RequireExactState(
        InstallerTransactionSnapshot expected,
        InstallerTransactionSnapshot? actual,
        string diagnosticCode)
    {
        expected.Validate();
        if (actual is null)
        {
            throw new InstallerProtocolException(diagnosticCode);
        }

        actual.Validate();
        if (actual != expected)
        {
            throw new InstallerProtocolException(diagnosticCode);
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}
