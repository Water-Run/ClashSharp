using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Exposes only read authority over an existing protected Installer transaction root.
/// </summary>
public sealed class WindowsInstallerProtectedTransactionReader :
    IInstallerTransactionReader,
    IDisposable
{
    private readonly WindowsInstallerTransactionRootGuard _rootGuard;
    private readonly FileInstallerTransactionStore _store;
    private bool _disposed;

    private WindowsInstallerProtectedTransactionReader(
        WindowsInstallerTransactionRootGuard rootGuard)
    {
        ArgumentNullException.ThrowIfNull(rootGuard);
        _rootGuard = rootGuard;
        _store = new FileInstallerTransactionStore(rootGuard.RootPath, rootGuard);
    }

    /// <summary>
    /// Creates a non-creating reader for the canonical ProgramData root and exact target SID.
    /// </summary>
    public static WindowsInstallerProtectedTransactionReader CreateDefault(string targetSid) =>
        new(WindowsInstallerTransactionRootGuard.CreateReadOnlyDefault(targetSid));

    /// <inheritdoc />
    public async Task<InstallerTransactionSnapshot?> LoadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _rootGuard
            .EnsureProtectedAsync(_rootGuard.RootPath, cancellationToken)
            .ConfigureAwait(false);
        if (!_rootGuard.IsProtectedRootPresent)
        {
            return null;
        }

        return await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Releases the read-only journal and pinned-directory leases.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _store.Dispose();
        _rootGuard.Dispose();
        _disposed = true;
    }

    internal static WindowsInstallerProtectedTransactionReader CreateForTesting(
        string programDataPath,
        string targetSid,
        IWindowsInstallerDirectoryNative native) =>
        new(WindowsInstallerTransactionRootGuard.CreateReadOnlyForTesting(
            programDataPath,
            targetSid,
            native));
}
