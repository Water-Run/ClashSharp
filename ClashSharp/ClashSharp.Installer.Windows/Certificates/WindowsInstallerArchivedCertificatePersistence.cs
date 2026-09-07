using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Certificates;

/// <summary>
/// Pins the existing private root and exposes only one authenticated account's archive. It never
/// creates directories or accesses the current owner's active ledger. The helper retains machine
/// exclusion and owns disposal after every archive operation and reconciliation has drained.
/// </summary>
internal sealed class WindowsInstallerArchivedCertificatePersistence : IInstallerArchivedCertificatePersistence, IDisposable
{
    private readonly string _rootPath;
    private readonly string _archivePath;
    private readonly IInstallerTransactionRootGuard _rootGuard;
    private readonly Func<bool> _isRootPresent;
    private readonly IWindowsInstallerPrivateJournalFileNative _files;
    private bool _disposed;

    private WindowsInstallerArchivedCertificatePersistence(string rootPath, string targetSid,
        IInstallerTransactionRootGuard rootGuard, Func<bool> isRootPresent, IWindowsInstallerPrivateJournalFileNative files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(rootGuard);
        ArgumentNullException.ThrowIfNull(isRootPresent);
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The archive root must be absolute.", nameof(rootPath));
        }
        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _archivePath = Path.Combine(_rootPath, WindowsInstallerCertificateArchiveLayout.GetFileName(targetSid));
        _rootGuard = rootGuard;
        _isRootPresent = isRootPresent;
        _files = files;
    }

    internal static WindowsInstallerArchivedCertificatePersistence CreateDefault(string authenticatedTargetSid)
    {
        InstallerProtocolValidation.ValidateTargetSid(authenticatedTargetSid);
        WindowsInstallerTransactionRootGuard guard = WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault();
        return new(guard.RootPath, authenticatedTargetSid, guard, () => guard.IsProtectedRootPresent,
            WindowsInstallerPrivateJournalFileNative.CreateForCertificateArchive(authenticatedTargetSid));
    }

    public async Task<byte[]?> ReadAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        return _isRootPresent()
            ? await _files.ReadAsync(_archivePath, cancellationToken).ConfigureAwait(false)
            : null;
    }

    public async Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await EnsureExistingAsync(cancellationToken).ConfigureAwait(false);
        await _files.WriteAtomicallyAsync(_archivePath, bytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeleteAsync(CancellationToken cancellationToken)
    {
        await EnsureExistingAsync(cancellationToken).ConfigureAwait(false);
        await _files.DeleteAsync(_archivePath, cancellationToken).ConfigureAwait(false);
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

    internal static WindowsInstallerArchivedCertificatePersistence CreateForTesting(string rootPath, string targetSid,
        IInstallerTransactionRootGuard rootGuard, Func<bool> isRootPresent, IWindowsInstallerPrivateJournalFileNative files) =>
        new(rootPath, targetSid, rootGuard, isRootPresent, files);

    private async Task EnsureExistingAsync(CancellationToken cancellationToken)
    {
        await EnsureAsync(cancellationToken).ConfigureAwait(false);
        if (!_isRootPresent())
        {
            throw new InstallerProtocolException("installer.certificate_archive.root_missing");
        }
    }

    private Task EnsureAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return _rootGuard.EnsureProtectedAsync(_rootPath, cancellationToken);
    }
}
