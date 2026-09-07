using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Retirement;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Owns a pinned private root for the fixed retired-account uninstall journal. Only the dedicated
/// authority creates missing private directories, after confirming the account-copy removal boundary.
/// </summary>
internal sealed class WindowsInstallerRetiredUninstallPersistence : IInstallerRetiredUninstallPersistence, IDisposable
{
    private readonly IInstallerTransactionRootGuard _guard;
    private readonly Func<bool> _isRootPresent;
    private readonly string _rootPath;
    private readonly string _journalPath;
    private readonly IWindowsInstallerPrivateJournalFileNative _files;
    private bool _disposed;

    private WindowsInstallerRetiredUninstallPersistence(string rootPath, IInstallerTransactionRootGuard guard,
        Func<bool> isRootPresent, IWindowsInstallerPrivateJournalFileNative files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(guard);
        ArgumentNullException.ThrowIfNull(isRootPresent);
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The private uninstall root must be absolute.", nameof(rootPath));
        }
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _journalPath = Path.Combine(_rootPath, InstallerRetiredUninstallStore.JournalFileName);
        _guard = guard;
        _isRootPresent = isRootPresent;
        _files = files;
    }

    internal static WindowsInstallerRetiredUninstallPersistence CreateDefault(bool createPrivateRoot)
    {
        WindowsInstallerTransactionRootGuard guard = createPrivateRoot
            ? WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferDefault()
            : WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault();
        return new(guard.RootPath, guard, () => guard.IsProtectedRootPresent,
            WindowsInstallerPrivateJournalFileNative.CreateForRetiredUninstall());
    }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _isRootPresent() ? await _files.ReadAsync(_journalPath, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await EnsureExistingAsync(cancellationToken).ConfigureAwait(false);
        await _files.WriteAtomicallyAsync(_journalPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await EnsureExistingAsync(cancellationToken).ConfigureAwait(false);
        await _files.DeleteAsync(_journalPath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            (_guard as IDisposable)?.Dispose();
        }
    }

    internal static WindowsInstallerRetiredUninstallPersistence CreateForTesting(string rootPath,
        IInstallerTransactionRootGuard guard, Func<bool> isRootPresent, IWindowsInstallerPrivateJournalFileNative files) =>
        new(rootPath, guard, isRootPresent, files);

    private async Task EnsureExistingAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!_isRootPresent())
        {
            throw new InstallerProtocolException("installer.retired_uninstall.root_missing");
        }
    }

    private Task EnsureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return _guard.EnsureProtectedAsync(_rootPath, cancellationToken);
    }
}
