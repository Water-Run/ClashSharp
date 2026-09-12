using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Keeps the exact verified uninstall recoverable while its ordinary journal and owned directories
/// are finalized. The helper's existing authority lease serializes this session; neither store is owned.
/// </summary>
internal sealed class WindowsInstallerCleanupTransactionStore : IInstallerTransactionStore
{
    private readonly IInstallerTransactionStore _inner;
    private readonly IWindowsInstallerDirectoryLedgerPersistence _ledger;
    private InstallerTransactionSnapshot? _clearedTerminal;

    internal WindowsInstallerCleanupTransactionStore(IInstallerTransactionStore inner,
        IWindowsInstallerDirectoryLedgerPersistence ledger)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(ledger);
        _inner = inner;
        _ledger = ledger;
    }

    public async Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        (InstallerTransactionSnapshot? active, WindowsInstallerDirectoryLedger? ledger) =
            await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (active is not null) { return active; }
        InstallerTransactionSnapshot? terminal = ledger?.Terminal;
        // Only this successfully cleared helper session suppresses its exact terminal checkpoint.
        // A new parent or helper wrapper still sees it until the directory finalizer deletes it.
        return terminal == _clearedTerminal ? null : terminal;
    }

    public async Task<InstallerTransactionSnapshot> SaveAsync(InstallerTransactionJournal journal,
        string? expectedCurrentHash, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        InstallerTransactionSnapshot desired = InstallerTransactionSnapshot.Create(journal);
        (InstallerTransactionSnapshot? _, WindowsInstallerDirectoryLedger? ledger) =
            await ReadAsync(cancellationToken).ConfigureAwait(false);
        if (ledger?.Terminal is { } terminal)
        {
            if (desired != terminal
                || !string.Equals(expectedCurrentHash, terminal.ContentHash, StringComparison.Ordinal)
                || _clearedTerminal is not null)
            {
                throw new InstallerProtocolException("installer.transaction.write_conflict");
            }
            // Final verification may acknowledge the same Verified checkpoint. Never resurrect
            // an ordinary journal or manufacture a Prepared transaction from terminal-only state.
            return terminal;
        }
        if (_clearedTerminal is not null)
        {
            throw new InstallerProtocolException("installer.transaction.write_conflict");
        }
        return await _inner.SaveAsync(journal, expectedCurrentHash, cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        InstallerProtocolValidation.ValidateLowerHex256(transactionId, "installer.transaction.id_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(expectedCurrentHash, "installer.transaction.content_hash_invalid");
        (InstallerTransactionSnapshot? active, WindowsInstallerDirectoryLedger? ledger) =
            await ReadAsync(cancellationToken).ConfigureAwait(false);
        InstallerTransactionSnapshot? current = active ?? ledger?.Terminal;
        if (current is null || current.Journal.Phase != InstallerTransactionPhase.Verified
            || !string.Equals(current.Journal.TransactionId, transactionId, StringComparison.Ordinal)
            || !string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.transaction.clear_conflict");
        }

        if (current.Journal.Operation != InstallerOperation.Uninstall)
        {
            await _inner.ClearVerifiedAsync(transactionId, expectedCurrentHash, cancellationToken).ConfigureAwait(false);
            return;
        }

        WindowsInstallerDirectoryLedger pending = (ledger ?? WindowsInstallerDirectoryLedger.Empty).BeginTerminal(current);
        await _ledger.SaveAsync(ledger, pending, cancellationToken).ConfigureAwait(false);
        await RequireTerminalAsync(pending, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (active is not null)
        {
            await _inner.ClearVerifiedAsync(transactionId, expectedCurrentHash, cancellationToken).ConfigureAwait(false);
        }

        InstallerTransactionSnapshot? remaining = await _inner.LoadAsync(cancellationToken).ConfigureAwait(false);
        remaining?.Validate();
        if (remaining is not null)
        {
            throw new InstallerProtocolException("installer.transaction.clear_not_observed");
        }
        await RequireTerminalAsync(pending, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _clearedTerminal = current;
    }

    private async Task<(InstallerTransactionSnapshot? Active, WindowsInstallerDirectoryLedger? Ledger)> ReadAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InstallerTransactionSnapshot? active = await _inner.LoadAsync(cancellationToken).ConfigureAwait(false);
        active?.Validate();
        WindowsInstallerDirectoryLedger? ledger = await _ledger.LoadAsync(cancellationToken).ConfigureAwait(false);
        ledger?.Validate();
        if (ledger?.Terminal is { } terminal
            && ((active is not null && active != terminal)
                || (_clearedTerminal is not null && _clearedTerminal != terminal)))
        {
            throw WindowsInstallerDirectoryLedger.Failure("terminal_identity_mismatch");
        }
        cancellationToken.ThrowIfCancellationRequested();
        return (active, ledger);
    }

    private async Task RequireTerminalAsync(WindowsInstallerDirectoryLedger expected, CancellationToken cancellationToken)
    {
        WindowsInstallerDirectoryLedger? observed = await _ledger.LoadAsync(cancellationToken).ConfigureAwait(false);
        observed?.Validate();
        if (observed is null || observed.Generation != expected.Generation || observed.Terminal != expected.Terminal
            || !observed.Directories.SequenceEqual(expected.Directories))
        {
            throw WindowsInstallerDirectoryLedger.Failure("terminal_not_observed");
        }
    }
}
