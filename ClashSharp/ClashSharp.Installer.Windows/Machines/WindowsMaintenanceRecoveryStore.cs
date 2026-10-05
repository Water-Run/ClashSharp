using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMaintenanceRecoveryStore : IDisposable
{
    Task<WindowsMaintenanceRecoveryRecord?> ReadAsync(CancellationToken cancellationToken);
    Task<WindowsMaintenanceRecoveryRecord> SaveAsync(WindowsMaintenanceRecoveryRecord? expected,
        WindowsMaintenanceRecoveryRecord desired, CancellationToken cancellationToken);
    Task RetireAsync(WindowsMaintenanceRecoveryRecord expected, InstallerTransactionSnapshot? successor, CancellationToken cancellationToken);
}

/// <summary>
/// Compares and atomically replaces one private recovery record under retained machine/App
/// authority. Mutations are observed after lost replies; retirement additionally requires an
/// absent or exact successor public transaction, plus unused capture or verified completion evidence.
/// </summary>
internal sealed class WindowsMaintenanceRecoveryStore : IWindowsMaintenanceRecoveryStore
{
    private readonly string _root;
    private readonly string _path;
    private readonly IInstallerTransactionRootGuard _readGuard;
    private readonly IInstallerTransactionRootGuard _writeGuard;
    private readonly Func<bool> _rootPresent;
    private readonly IWindowsInstallerPrivateJournalFileNative _files;
    private readonly IWindowsInstallerAuthorityLease _machine;
    private readonly IWindowsInstallerApplicationLease _application;
    private readonly IInstallerTransactionReader _transactions;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal WindowsMaintenanceRecoveryStore(string root, IInstallerTransactionRootGuard readGuard,
        IInstallerTransactionRootGuard writeGuard, Func<bool> rootPresent, IWindowsInstallerPrivateJournalFileNative files,
        IWindowsInstallerAuthorityLease machine, IWindowsInstallerApplicationLease application, IInstallerTransactionReader transactions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Path.IsPathFullyQualified(root)) { throw new ArgumentException("Recovery root must be absolute.", nameof(root)); }
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _path = Path.Combine(_root, WindowsMaintenanceRecoveryRecord.FileName);
        _readGuard = readGuard ?? throw new ArgumentNullException(nameof(readGuard));
        _writeGuard = writeGuard ?? throw new ArgumentNullException(nameof(writeGuard));
        _rootPresent = rootPresent ?? throw new ArgumentNullException(nameof(rootPresent));
        _files = files ?? throw new ArgumentNullException(nameof(files));
        _machine = machine ?? throw new ArgumentNullException(nameof(machine));
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _transactions = transactions ?? throw new ArgumentNullException(nameof(transactions));
    }

    internal static WindowsMaintenanceRecoveryStore CreateDefault(IWindowsInstallerAuthorityLease machine,
        IWindowsInstallerApplicationLease application, IInstallerTransactionReader transactions)
    {
        var read = WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault();
        var write = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferDefault();
        return new(read.RootPath, read, write, () => read.IsProtectedRootPresent,
            WindowsInstallerPrivateJournalFileNative.CreateForMaintenanceRecovery(), machine, application, transactions);
    }

    public async Task<WindowsMaintenanceRecoveryRecord?> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
            WindowsMaintenanceRecoveryRecord? observed = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
            return observed;
        }
        finally { _gate.Release(); }
    }

    public async Task<WindowsMaintenanceRecoveryRecord> SaveAsync(WindowsMaintenanceRecoveryRecord? expected,
        WindowsMaintenanceRecoveryRecord desired, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(desired);
        if (desired.Intent.TargetSid != _application.TargetSid) { throw Conflict(); }
        byte[] bytes = desired.Serialize();
        bool entered = false;
        try
        {
            if (expected is null)
            {
                if (desired.Stage != WindowsMaintenanceRecoveryStage.Captured || desired.Generation != 1) { throw Conflict(); }
            }
            else if (!Equal(expected, desired)) { desired.RequireSuccessorOf(expected); }
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
            WindowsMaintenanceRecoveryRecord? current = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (Equal(current, desired))
            {
                await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
                return current!;
            }
            if (!Equal(current, expected)) { throw Conflict(); }
            await _writeGuard.EnsureProtectedAsync(_root, cancellationToken).ConfigureAwait(false);
            current = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (!Equal(current, expected)) { throw Conflict(); }
            await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try { await _files.WriteAtomicallyAsync(_path, bytes, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (IsRecoverable(exception)) { /* Authoritative read below reconciles the admitted write. */ }
            try
            {
                WindowsMaintenanceRecoveryRecord? actual = await ReadLockedAsync(CancellationToken.None).ConfigureAwait(false);
                await ReverifyAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
                if (!Equal(actual, desired)) { throw Uncertain(); }
                return actual!;
            }
            catch (Exception exception) when (IsRecoverable(exception)) { throw Uncertain(); }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            if (entered) { _gate.Release(); }
        }
    }

    public async Task RetireAsync(WindowsMaintenanceRecoveryRecord expected, InstallerTransactionSnapshot? successor, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(expected);
        if (!expected.CanRetire) { throw Conflict(); }
        successor?.Validate();
        if (successor?.Journal.TransactionId == expected.Intent.TransactionId) { throw Conflict(); }
        if (successor is not null && successor.Journal.TargetSid != _application.TargetSid) { throw Conflict(); }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
            if (await _transactions.LoadAsync(cancellationToken).ConfigureAwait(false) != successor) { throw Conflict(); }
            WindowsMaintenanceRecoveryRecord? current = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            if (!Equal(current, expected)) { throw Conflict(); }
            await ReverifyAuthorityAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            try { await _files.DeleteAsync(_path, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (IsRecoverable(exception)) { /* Verify absence before acknowledging retirement. */ }
            try
            {
                if (await ReadLockedAsync(CancellationToken.None).ConfigureAwait(false) is not null
                    || await _transactions.LoadAsync(CancellationToken.None).ConfigureAwait(false) != successor) { throw Uncertain(); }
                await ReverifyAuthorityAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsRecoverable(exception)) { throw Uncertain(); }
        }
        finally { _gate.Release(); }
    }

    private async Task<WindowsMaintenanceRecoveryRecord?> ReadLockedAsync(CancellationToken cancellationToken)
    {
        await _readGuard.EnsureProtectedAsync(_root, cancellationToken).ConfigureAwait(false);
        if (!_rootPresent()) { return null; }
        byte[]? bytes = await _files.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
        if (bytes is null) { return null; }
        try { return WindowsMaintenanceRecoveryRecord.Parse(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task ReverifyAuthorityAsync(CancellationToken cancellationToken)
    {
        await _machine.ReverifyAsync(cancellationToken).ConfigureAwait(false);
        _application.Reverify(cancellationToken);
    }

    private static bool Equal(WindowsMaintenanceRecoveryRecord? left, WindowsMaintenanceRecoveryRecord? right)
    {
        if (left is null || right is null) { return left is null && right is null; }
        byte[] first = left.Serialize();
        byte[]? second = null;
        try { second = right.Serialize(); return first.AsSpan().SequenceEqual(second); }
        finally { CryptographicOperations.ZeroMemory(first); if (second is not null) { CryptographicOperations.ZeroMemory(second); } }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _gate.Dispose();
        (_writeGuard as IDisposable)?.Dispose();
        (_readGuard as IDisposable)?.Dispose();
    }
    private static InstallerProtocolException Conflict() => new("installer.recovery.record_write_conflict");
    private static InstallerStateUncertainException Uncertain() => new("installer.recovery.record_commit_uncertain");
    private static bool IsRecoverable(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException);
}
