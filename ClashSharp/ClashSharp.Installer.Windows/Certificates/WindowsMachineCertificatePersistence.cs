using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Keeps machine trust ownership in the existing private authority root, independently of current
/// owner ACL transfers. The helper owns this guard until all certificate work has drained.
/// </summary>
internal sealed class WindowsMachineCertificatePersistence : IInstallerMachineCertificatePersistence, IDisposable
{
    private readonly string _rootPath;
    private readonly string _path;
    private readonly IInstallerTransactionRootGuard _readGuard;
    private readonly IInstallerTransactionRootGuard _writeGuard;
    private readonly Func<bool> _isRootPresent;
    private readonly IWindowsInstallerPrivateJournalFileNative _files;
    private bool _disposed;

    internal WindowsMachineCertificatePersistence(string rootPath, IInstallerTransactionRootGuard readGuard,
        IInstallerTransactionRootGuard writeGuard, Func<bool> isRootPresent,
        IWindowsInstallerPrivateJournalFileNative files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(readGuard);
        ArgumentNullException.ThrowIfNull(writeGuard);
        ArgumentNullException.ThrowIfNull(isRootPresent);
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The machine certificate root must be absolute.", nameof(rootPath));
        }
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _path = Path.Combine(_rootPath, InstallerMachineCertificateOwnership.FileName);
        _readGuard = readGuard;
        _writeGuard = writeGuard;
        _isRootPresent = isRootPresent;
        _files = files;
    }

    internal static WindowsMachineCertificatePersistence CreateDefault()
    {
        WindowsInstallerTransactionRootGuard readGuard = WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault();
        WindowsInstallerTransactionRootGuard writeGuard = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferDefault();
        return new(readGuard.RootPath, readGuard, writeGuard, () => readGuard.IsProtectedRootPresent,
            WindowsInstallerPrivateJournalFileNative.CreateForMachineCertificate());
    }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(_readGuard, cancellationToken).ConfigureAwait(false);
        return _isRootPresent() ? await _files.ReadAsync(_path, cancellationToken).ConfigureAwait(false) : null;
    }

    public async Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        _ = InstallerMachineCertificateOwnership.Parse(bytes.Span);
        await EnsureAsync(_writeGuard, cancellationToken).ConfigureAwait(false);
        await _files.WriteAtomicallyAsync(_path, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(_readGuard, cancellationToken).ConfigureAwait(false);
        if (_isRootPresent())
        {
            await _files.DeleteAsync(_path, cancellationToken).ConfigureAwait(false);
        }
    }

    private Task EnsureAsync(IInstallerTransactionRootGuard guard, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return guard.EnsureProtectedAsync(_rootPath, cancellationToken);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            (_writeGuard as IDisposable)?.Dispose();
            (_readGuard as IDisposable)?.Dispose();
        }
    }
}
