using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Owns the fixed private journal root until the elevated session has drained every store call.
/// Creation is side-effect free; each read or mutation first revalidates the pinned private chain.
/// </summary>
internal sealed class WindowsInstallerOwnerTransferPersistence : IInstallerOwnerTransferPersistence, IDisposable
{
    private readonly IInstallerTransactionRootGuard _rootGuard;
    private readonly IWindowsInstallerPrivateJournalFileNative _files;
    private readonly string _rootPath;
    private readonly string _journalPath;
    private bool _disposed;

    private WindowsInstallerOwnerTransferPersistence(
        string rootPath,
        IInstallerTransactionRootGuard rootGuard,
        IWindowsInstallerPrivateJournalFileNative files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(rootGuard);
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The private state root must be absolute.", nameof(rootPath));
        }

        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _journalPath = Path.Combine(_rootPath, InstallerOwnerTransferStateLayout.JournalFileName);
        _rootGuard = rootGuard;
        _files = files;
    }

    internal static WindowsInstallerOwnerTransferPersistence CreateDefault()
    {
        WindowsInstallerTransactionRootGuard guard = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferDefault();
        return new(guard.RootPath, guard, WindowsInstallerPrivateJournalFileNative.Instance);
    }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        await EnsurePrivateAsync(cancellationToken).ConfigureAwait(false);
        return await _files.ReadAsync(_journalPath, cancellationToken).ConfigureAwait(false);
    }

    public async Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await EnsurePrivateAsync(cancellationToken).ConfigureAwait(false);
        await _files.WriteAtomicallyAsync(_journalPath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await EnsurePrivateAsync(cancellationToken).ConfigureAwait(false);
        await _files.DeleteAsync(_journalPath, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        (_rootGuard as IDisposable)?.Dispose();
    }

    internal static WindowsInstallerOwnerTransferPersistence CreateForTesting(
        string rootPath,
        IInstallerTransactionRootGuard rootGuard,
        IWindowsInstallerPrivateJournalFileNative files) => new(rootPath, rootGuard, files);

    private Task EnsurePrivateAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return _rootGuard.EnsureProtectedAsync(_rootPath, cancellationToken);
    }
}
