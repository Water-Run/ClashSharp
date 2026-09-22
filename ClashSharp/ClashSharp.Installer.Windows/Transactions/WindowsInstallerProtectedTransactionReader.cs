using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Transactions;

/// <summary>
/// Exposes only read authority over an existing protected Installer transaction root.
/// </summary>
public sealed class WindowsInstallerProtectedTransactionReader :
    IInstallerTransactionReader,
    IDisposable
{
    private readonly object _gate = new();
    private readonly Func<WindowsInstallerTransactionRootGuard> _createRootGuard;
    private readonly IWindowsInstallerDirectoryLedgerPersistence? _directoryLedger;
    private int _activeReads;
    private bool _disposed;

    private WindowsInstallerProtectedTransactionReader(
        Func<WindowsInstallerTransactionRootGuard> createRootGuard,
        IWindowsInstallerDirectoryLedgerPersistence? directoryLedger)
    {
        ArgumentNullException.ThrowIfNull(createRootGuard);
        _createRootGuard = createRootGuard;
        _directoryLedger = directoryLedger;
        // Preserve eager path/SID validation without opening or creating directories.
        using WindowsInstallerTransactionRootGuard validation = _createRootGuard();
    }

    /// <summary>
    /// Creates a non-creating reader for the canonical ProgramData root and exact target SID.
    /// </summary>
    public static WindowsInstallerProtectedTransactionReader CreateDefault(string targetSid) =>
        new(() => WindowsInstallerTransactionRootGuard.CreateReadOnlyDefault(targetSid),
            WindowsInstallerDirectoryLedgerPersistence.CreateDefault());

    /// <inheritdoc />
    public async Task<InstallerTransactionSnapshot?> LoadAsync(
        CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            _activeReads++;
        }

        try
        {
            // Pin the complete chain through the file read, parsing, and content hash. No handles
            // survive this observation, so the elevated helper can finalize an empty state root.
            using WindowsInstallerTransactionRootGuard rootGuard = _createRootGuard();
            await rootGuard.EnsureProtectedAsync(rootGuard.RootPath, cancellationToken)
                .ConfigureAwait(false);
            InstallerTransactionSnapshot? active = null;
            if (rootGuard.IsProtectedRootPresent)
            {
                using var store = new FileInstallerTransactionStore(rootGuard.RootPath, rootGuard);
                active = await store.LoadAsync(cancellationToken).ConfigureAwait(false);
            }

            // The terminal is outside the removable root. Its presence must remain observable
            // after a crash between clearing the active journal and completing directory cleanup.
            WindowsInstallerDirectoryLedger? ledger = _directoryLedger is null ? null
                : await _directoryLedger.LoadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return WindowsInstallerCleanupTransactionReader.Resolve(active, ledger);
        }
        finally
        {
            lock (_gate)
            {
                _activeReads--;
                Monitor.PulseAll(_gate);
            }
        }
    }

    /// <summary>Rejects new reads and waits for accepted reads to release their pinned leases.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            while (_activeReads != 0)
            {
                Monitor.Wait(_gate);
            }
        }
    }

    internal static WindowsInstallerProtectedTransactionReader CreateForTesting(
        string programDataPath,
        string targetSid,
        IWindowsInstallerDirectoryNative native,
        IWindowsInstallerDirectoryLedgerPersistence? directoryLedger = null) =>
        new(() => WindowsInstallerTransactionRootGuard.CreateReadOnlyForTesting(
            programDataPath,
            targetSid,
            native), directoryLedger);
}
