using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Owns the journal and certificate ledger stores under one pinned ProgramData security boundary.
/// </summary>
public sealed class WindowsInstallerProtectedStateStores : IDisposable
{
    private readonly IDisposable? _rootGuardLifetime;
    private readonly FileInstallerTransactionStore _transactionStore;
    private readonly IInstallerTransactionStore _transactions;
    private readonly FileInstallerCertificateOwnershipStore _certificateOwnershipStore;
    private bool _disposed;

    private WindowsInstallerProtectedStateStores(
        string rootPath,
        IInstallerTransactionRootGuard rootGuard,
        IWindowsInstallerDirectoryLedgerPersistence? directoryLedger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(rootGuard);
        RootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _rootGuardLifetime = rootGuard as IDisposable;
        _transactionStore = new FileInstallerTransactionStore(RootPath, rootGuard);
        _transactions = directoryLedger is null ? _transactionStore
            : new WindowsInstallerCleanupTransactionStore(_transactionStore, directoryLedger);
        _certificateOwnershipStore = new FileInstallerCertificateOwnershipStore(
            RootPath,
            rootGuard);
    }

    /// <summary>Gets the fixed ProgramData root shared by both durable stores.</summary>
    public string RootPath { get; }

    /// <summary>Gets the compare-and-swap installer transaction journal store.</summary>
    public IInstallerTransactionStore Transactions => _transactions;

    /// <summary>Gets the least-authority transaction view intended for the unelevated parent.</summary>
    public IInstallerTransactionReader TransactionReader => _transactions;

    /// <summary>Gets the compare-and-swap certificate ownership ledger store.</summary>
    public IInstallerCertificateOwnershipStore CertificateOwnership =>
        _certificateOwnershipStore;

    /// <summary>Creates the protected stores for one canonical target-user SID.</summary>
    /// <param name="targetSid">Interactive user allowed to read recovery state.</param>
    /// <returns>Both durable stores and their shared directory-handle lease owner.</returns>
    public static WindowsInstallerProtectedStateStores CreateDefault(string targetSid)
    {
        WindowsInstallerTransactionRootGuard guard =
            WindowsInstallerTransactionRootGuard.CreateDefault(targetSid);
        try
        {
            return new WindowsInstallerProtectedStateStores(guard.RootPath, guard,
                WindowsInstallerDirectoryLedgerPersistence.CreateDefault());
        }
        catch
        {
            guard.Dispose();
            throw;
        }
    }

    /// <summary>Disposes both stores before releasing their shared rename-blocking handles.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _certificateOwnershipStore.Dispose();
        _transactionStore.Dispose();
        _rootGuardLifetime?.Dispose();
        _disposed = true;
    }

    internal static WindowsInstallerProtectedStateStores CreateForTesting(
        string rootPath,
        IInstallerTransactionRootGuard rootGuard,
        IWindowsInstallerDirectoryLedgerPersistence? directoryLedger = null) =>
        new(rootPath, rootGuard, directoryLedger);
}
