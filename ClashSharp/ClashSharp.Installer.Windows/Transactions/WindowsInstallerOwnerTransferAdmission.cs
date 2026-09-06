using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Windows.Transactions;

internal interface IWindowsInstallerOwnerTransferAdmission
{
    Task EnsureOrdinaryActionAllowedAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Rejects ordinary helper authority while private transfer evidence exists, without creating
/// directories, parsing credentials or trusting an unreadable root as absence. The caller keeps
/// the machine-wide authority lease throughout this check and all subsequent ordinary operations.
/// </summary>
internal sealed class WindowsInstallerOwnerTransferAdmission : IWindowsInstallerOwnerTransferAdmission
{
    private readonly Func<WindowsInstallerTransactionRootGuard> _createRootGuard;
    private readonly IWindowsInstallerPrivateJournalPresenceNative _files;

    private WindowsInstallerOwnerTransferAdmission(
        Func<WindowsInstallerTransactionRootGuard> createRootGuard,
        IWindowsInstallerPrivateJournalPresenceNative files)
    {
        ArgumentNullException.ThrowIfNull(createRootGuard);
        ArgumentNullException.ThrowIfNull(files);
        _createRootGuard = createRootGuard;
        _files = files;
    }

    internal static WindowsInstallerOwnerTransferAdmission CreateDefault() =>
        new(WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault,
            WindowsInstallerPrivateJournalFileNative.Instance);

    public async Task EnsureOrdinaryActionAllowedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using WindowsInstallerTransactionRootGuard guard = _createRootGuard();
        await guard.EnsureProtectedAsync(guard.RootPath, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (guard.IsProtectedRootPresent && _files.IsPresent(
            Path.Combine(guard.RootPath, InstallerOwnerTransferStateLayout.JournalFileName), cancellationToken))
        {
            // Every phase, empty document and malformed document blocks ordinary admission.
            // Only the dedicated transfer authority may interpret or recover this private state.
            throw new InstallerProtocolException("installer.owner_transfer.pending");
        }
    }

    internal static WindowsInstallerOwnerTransferAdmission CreateForTesting(
        Func<WindowsInstallerTransactionRootGuard> createRootGuard,
        IWindowsInstallerPrivateJournalPresenceNative files) => new(createRootGuard, files);
}
