using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Retirement;

namespace ClashSharp.Installer.Windows.Transactions;

internal interface IWindowsInstallerRetiredUninstallAdmission
{
    Task EnsureNoRetiredUninstallAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Blocks ordinary installation and owner transfer whenever private account-copy removal evidence
/// exists, including malformed or empty evidence. The caller retains machine-wide exclusion;
/// only the dedicated uninstall helper may interpret or recover this state.
/// </summary>
internal sealed class WindowsInstallerRetiredUninstallAdmission : IWindowsInstallerRetiredUninstallAdmission
{
    private readonly Func<WindowsInstallerTransactionRootGuard> _createGuard;
    private readonly IWindowsInstallerPrivateJournalPresenceNative _files;

    private WindowsInstallerRetiredUninstallAdmission(Func<WindowsInstallerTransactionRootGuard> createGuard,
        IWindowsInstallerPrivateJournalPresenceNative files)
    {
        ArgumentNullException.ThrowIfNull(createGuard);
        ArgumentNullException.ThrowIfNull(files);
        _createGuard = createGuard;
        _files = files;
    }

    internal static WindowsInstallerRetiredUninstallAdmission CreateDefault() =>
        new(WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault,
            WindowsInstallerPrivateJournalFileNative.CreateForRetiredUninstall());

    public async Task EnsureNoRetiredUninstallAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using WindowsInstallerTransactionRootGuard guard = _createGuard();
        await guard.EnsureProtectedAsync(guard.RootPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (guard.IsProtectedRootPresent && _files.IsPresent(
            Path.Combine(guard.RootPath, InstallerRetiredUninstallStore.JournalFileName), cancellationToken))
        {
            throw new InstallerProtocolException("installer.retired_uninstall.pending");
        }
    }

    internal static WindowsInstallerRetiredUninstallAdmission CreateForTesting(
        Func<WindowsInstallerTransactionRootGuard> createGuard, IWindowsInstallerPrivateJournalPresenceNative files) =>
        new(createGuard, files);
}
