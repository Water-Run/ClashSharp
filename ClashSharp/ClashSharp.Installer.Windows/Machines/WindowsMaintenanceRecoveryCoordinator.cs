using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMaintenanceOriginalSession : IDisposable
{
    Task<WindowsMaintenanceOriginalBaseline> CaptureAsync(InstallerTransactionJournal intent, CancellationToken cancellationToken);
    Task VerifyOriginalAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source,
        InstallerTransactionSnapshot? expectedPublic, CancellationToken cancellationToken);
    Task RestoreServiceAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source, CancellationToken cancellationToken);
    Task VerifyRestoredAsync(WindowsMaintenanceOriginalBaseline baseline, InstallerTransactionSnapshot source,
        InstallerTransactionSnapshot? expectedPublic, CancellationToken cancellationToken);
}

internal interface IWindowsMaintenanceOriginalSessionFactory
{
    Task<IWindowsMaintenanceOriginalSession> OpenAsync(InstallerRequest request, IInstallerReleaseLease release, CancellationToken cancellationToken);
}

/// <summary>
/// Owns private decisions and original file pins for one authenticated helper lifetime. Original
/// pins survive return to the public authority session so its terminal write/clear remains guarded.
/// Explicit candidate continuation releases those pins only after recording that decision.
/// </summary>
internal sealed class WindowsMaintenanceRecoveryCoordinator : IDisposable
{
    private readonly IWindowsMaintenanceRecoveryStore _store;
    private readonly IInstallerTransactionReader _transactions;
    private readonly IWindowsMaintenanceOriginalSessionFactory _sessions;
    private IWindowsMaintenanceOriginalSession? _held;
    private bool _disposed;

    internal WindowsMaintenanceRecoveryCoordinator(IWindowsMaintenanceRecoveryStore store,
        IInstallerTransactionReader transactions, IWindowsMaintenanceOriginalSessionFactory sessions)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
        _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
    }

    internal async Task CaptureInitialAsync(InstallerMachineHelperCommand command, IInstallerReleaseLease release, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InstallerTransactionSnapshot state = command.ToDurableState();
        if (command.Verb != InstallerMachineHelperVerb.Prepare) { throw Failure("phase_invalid"); }
        await RequirePublicAsync(null, cancellationToken).ConfigureAwait(false);
        WindowsMaintenanceRecoveryRecord? previous = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (previous is not null)
        {
            if (previous.Intent == state.Journal && previous.Stage == WindowsMaintenanceRecoveryStage.Captured) { return; }
            await _store.RetireAsync(previous, null, cancellationToken).ConfigureAwait(false);
        }
        if (!IsOrdinaryRepair(state.Journal)) { return; }
        WindowsMaintenanceOriginalBaseline? baseline = null;
        string? unavailable = null;
        try
        {
            using IWindowsMaintenanceOriginalSession original = await _sessions.OpenAsync(Request(state), release, cancellationToken).ConfigureAwait(false);
            baseline = await original.CaptureAsync(state.Journal, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallerProtocolException exception) when (!exception.DiagnosticCode.StartsWith("installer.release.", StringComparison.Ordinal))
        {
            // A damaged installation must remain repairable. This durable marker prevents later
            // retries from mistaking fenced or partially repaired state for the original.
            unavailable = "installer.recovery.original_capture_unavailable";
        }
        await RequirePublicAsync(null, cancellationToken).ConfigureAwait(false);
        await _store.SaveAsync(null, WindowsMaintenanceRecoveryRecord.Capture(state.Journal, baseline, unavailable), cancellationToken).ConfigureAwait(false);
    }

    internal async Task BeforeOrdinaryAsync(InstallerMachineHelperCommand command,
        InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay) { return; }
        InstallerTransactionSnapshot state = command.ToDurableState();
        await RequirePublicAsync(state, cancellationToken).ConfigureAwait(false);
        WindowsMaintenanceRecoveryRecord? record = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (record is not null && record.Intent.TransactionId != state.Journal.TransactionId)
        {
            await _store.RetireAsync(record, state, cancellationToken).ConfigureAwait(false);
            record = null;
        }
        if (!IsOrdinaryRepair(state.Journal)) { return; }
        if (record is null)
        {
            // A legacy/interrupted Prepared transaction has no proof that preparation never ran.
            // Continue safely, but never capture a new original observation from that transaction.
            record = await _store.SaveAsync(null, WindowsMaintenanceRecoveryRecord.Capture(Initial(state), null,
                "installer.recovery.original_baseline_missing"), cancellationToken).ConfigureAwait(false);
        }
        record.RequireTransaction(state);
        await _store.SaveAsync(record, record.Continue(state), cancellationToken).ConfigureAwait(false);
        ReleaseHeld();
    }

    internal async Task BeforeCandidateClearAsync(InstallerMachineHelperCommand command, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InstallerTransactionSnapshot terminal = command.ToDurableState();
        if (command.Verb != InstallerMachineHelperVerb.Clear || terminal.Journal.Phase != InstallerTransactionPhase.Verified)
        {
            throw Failure("phase_invalid");
        }
        WindowsMaintenanceRecoveryRecord? record = await _store.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (record is null) { return; }
        if (record.Intent.TransactionId != terminal.Journal.TransactionId)
        {
            await _store.RetireAsync(record, await _transactions.LoadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
            return;
        }
        record.RequireTransaction(terminal);
        InstallerTransactionSnapshot? current = await _transactions.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (current is not null && current != terminal) { throw Failure("transaction_changed"); }
        await _store.SaveAsync(record, record.PrepareCandidateClear(terminal), cancellationToken).ConfigureAwait(false);
        ReleaseHeld();
    }

    internal async Task ExecuteOriginalAsync(InstallerMachineHelperCommand command, IInstallerReleaseLease release,
        InstallerMachineHelperSessionDisposition disposition, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InstallerTransactionSnapshot requested = command.ToDurableState();
        if (command.Verb is not (InstallerMachineHelperVerb.RestoreOriginal or InstallerMachineHelperVerb.ClearOriginal)) { throw Failure("phase_invalid"); }
        WindowsMaintenanceRecoveryRecord record = await _store.ReadAsync(cancellationToken).ConfigureAwait(false)
            ?? throw Failure("original_baseline_missing");
        record.RequireTransaction(requested);
        WindowsMaintenanceOriginalBaseline baseline = record.Original ?? throw Failure("original_baseline_unavailable");
        bool restoring = command.Verb == InstallerMachineHelperVerb.RestoreOriginal
            && disposition == InstallerMachineHelperSessionDisposition.Execute;
        InstallerTransactionSnapshot? expectedPublic = restoring ? requested
            : command.Verb == InstallerMachineHelperVerb.ClearOriginal && disposition == InstallerMachineHelperSessionDisposition.VerifyCommittedReplay
                ? null : command.GetExpectedSuccessfulState();
        await RequirePublicAsync(expectedPublic, cancellationToken).ConfigureAwait(false);
        if (!restoring)
        {
            if (record.Stage is not (WindowsMaintenanceRecoveryStage.OriginalVerified or WindowsMaintenanceRecoveryStage.OriginalClearReady)
                || record.RestoredTerminal() != command.GetExpectedSuccessfulState()
                || expectedPublic is null && record.Stage != WindowsMaintenanceRecoveryStage.OriginalClearReady)
            {
                throw Failure("original_completion_missing");
            }
        }
        IWindowsMaintenanceOriginalSession original = await _sessions.OpenAsync(Request(requested), release, cancellationToken).ConfigureAwait(false);
        IWindowsMaintenanceOriginalSession? previous = _held;
        _held = original;
        previous?.Dispose();
        if (restoring)
        {
            await original.VerifyOriginalAsync(baseline, requested, requested, cancellationToken).ConfigureAwait(false);
            record = await _store.SaveAsync(record, record.Preserve(requested), cancellationToken).ConfigureAwait(false);
            if (record.Stage != WindowsMaintenanceRecoveryStage.OriginalVerified)
            {
                await original.RestoreServiceAsync(baseline, requested, cancellationToken).ConfigureAwait(false);
            }
            // Finish proof and private acknowledgement after admitted service effects even if the
            // UI cancels. Core may still leave the public terminal pending for a later retry.
            await original.VerifyRestoredAsync(baseline, requested, requested, CancellationToken.None).ConfigureAwait(false);
            await _store.SaveAsync(record, record.VerifyOriginal(), CancellationToken.None).ConfigureAwait(false);
            return;
        }
        await original.VerifyRestoredAsync(baseline, record.RestorationSource!, expectedPublic, cancellationToken).ConfigureAwait(false);
        if (command.Verb == InstallerMachineHelperVerb.ClearOriginal && expectedPublic is not null)
        {
            await _store.SaveAsync(record, record.PrepareOriginalClear(expectedPublic), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task RequirePublicAsync(InstallerTransactionSnapshot? expected, CancellationToken cancellationToken)
    {
        InstallerTransactionSnapshot? actual = await _transactions.LoadAsync(cancellationToken).ConfigureAwait(false);
        actual?.Validate();
        if (actual != expected) { throw Failure("transaction_changed"); }
    }
    private static bool IsOrdinaryRepair(InstallerTransactionJournal journal) => journal.Operation == InstallerOperation.Repair && !journal.AllowReassociation;
    private static InstallerTransactionJournal Initial(InstallerTransactionSnapshot state) => state.Journal with { Phase = InstallerTransactionPhase.Prepared, Generation = 1 };
    private static InstallerRequest Request(InstallerTransactionSnapshot state) => new(state.Journal.Operation, state.Journal.TargetSid,
        state.Journal.AllowReassociation, state.Journal.ExpectedPackageVersion, state.Journal.InstallerPayloadSha256);
    private void ReleaseHeld() { IWindowsMaintenanceOriginalSession? held = _held; _held = null; held?.Dispose(); }
    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { ReleaseHeld(); }
        finally { _store.Dispose(); }
    }
    private static InstallerProtocolException Failure(string suffix) => new("installer.recovery." + suffix);
}
