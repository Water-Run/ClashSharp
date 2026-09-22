using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>Observes a terminal uninstall without granting journal or directory mutation authority.</summary>
internal sealed class WindowsInstallerCleanupTransactionReader(
    IInstallerTransactionReader active,
    IWindowsInstallerDirectoryLedgerPersistence ledger) : IInstallerTransactionReader
{
    private readonly IInstallerTransactionReader _active = active ?? throw new ArgumentNullException(nameof(active));
    private readonly IWindowsInstallerDirectoryLedgerPersistence _ledger = ledger ?? throw new ArgumentNullException(nameof(ledger));

    public async Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        InstallerTransactionSnapshot? current = await _active.LoadAsync(cancellationToken).ConfigureAwait(false);
        WindowsInstallerDirectoryLedger? directories = await _ledger.LoadAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return Resolve(current, directories);
    }

    internal static InstallerTransactionSnapshot? Resolve(
        InstallerTransactionSnapshot? active, WindowsInstallerDirectoryLedger? ledger)
    {
        active?.Validate();
        ledger?.Validate();
        if (active is not null && ledger?.Terminal is { } terminal && active != terminal)
        {
            throw WindowsInstallerDirectoryLedger.Failure("terminal_identity_mismatch");
        }
        return active ?? ledger?.Terminal;
    }
}
