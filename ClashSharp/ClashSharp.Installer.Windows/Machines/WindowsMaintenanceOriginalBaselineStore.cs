using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Stores the first complete original installation in the authority-only root. The caller retains
/// authenticated cross-process exclusion throughout this store's lifetime. Neither a later
/// transaction nor a fresh observation may overwrite the original evidence; disposal keeps it.
/// </summary>
internal sealed class WindowsMaintenanceOriginalBaselineStore : IDisposable
{
    private readonly string _root;
    private readonly string _path;
    private readonly IInstallerTransactionRootGuard _readGuard;
    private readonly IInstallerTransactionRootGuard _writeGuard;
    private readonly Func<bool> _isRootPresent;
    private readonly IWindowsInstallerPrivateJournalFileNative _files;
    private readonly IWindowsMaintenanceRecoveryAuthorityLease _authority;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal WindowsMaintenanceOriginalBaselineStore(string root, IInstallerTransactionRootGuard readGuard,
        IInstallerTransactionRootGuard writeGuard, Func<bool> isRootPresent,
        IWindowsInstallerPrivateJournalFileNative files, IWindowsMaintenanceRecoveryAuthorityLease authority)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(readGuard);
        ArgumentNullException.ThrowIfNull(writeGuard);
        ArgumentNullException.ThrowIfNull(isRootPresent);
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(authority);
        if (!Path.IsPathFullyQualified(root)) { throw new ArgumentException("The private recovery root must be absolute.", nameof(root)); }
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _path = Path.Combine(_root, WindowsMaintenanceOriginalBaseline.FileName);
        _readGuard = readGuard;
        _writeGuard = writeGuard;
        _isRootPresent = isRootPresent;
        _files = files;
        _authority = authority;
    }

    internal static WindowsMaintenanceOriginalBaselineStore CreateDefault(IWindowsMaintenanceRecoveryAuthorityLease authority)
    {
        WindowsInstallerTransactionRootGuard read = WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault();
        WindowsInstallerTransactionRootGuard write = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferDefault();
        return new(read.RootPath, read, write, () => read.IsProtectedRootPresent,
            WindowsInstallerPrivateJournalFileNative.CreateForOriginalInstallationBaseline(), authority);
    }

    internal async Task<WindowsMaintenanceOriginalBaseline?> ReadAsync(WindowsMachineDeploymentPlan plan,
        InstallerTransactionJournal current, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(current);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            WindowsMaintenanceOriginalBaseline? baseline = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            baseline?.RequireBoundary(plan, current);
            await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            return baseline;
        }
        finally { _gate.Release(); }
    }

    internal async Task<WindowsMaintenanceOriginalBaseline> CaptureOnceAsync(WindowsMachineDeploymentPlan plan,
        WindowsMaintenanceOriginalBaseline baseline, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(baseline);
        baseline.RequireBoundary(plan, baseline.Service.Intent);
        byte[] expected = baseline.Serialize();
        bool entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            WindowsMaintenanceOriginalBaseline? existing = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireEqual(existing, expected);
                await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }
            await _writeGuard.EnsureProtectedAsync(_root, cancellationToken).ConfigureAwait(false);
            existing = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireEqual(existing, expected);
                await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
                return existing;
            }
            await _authority.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = null;
            try { await _files.WriteAtomicallyAsync(_path, expected, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (IsRecoverable(exception)) { failure = exception; }
            // Once admitted, use authoritative uncancelled observation even when the write reply
            // or UI cancellation was lost. An expired authority cannot turn a write into success.
            try
            {
                WindowsMaintenanceOriginalBaseline? observed = await ReadLockedAsync(CancellationToken.None).ConfigureAwait(false);
                if (observed is null)
                {
                    if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
                    {
                        throw new OperationCanceledException(cancellationToken);
                    }
                    throw new InstallerStateUncertainException("installer.recovery.original_baseline_write_uncertain");
                }
                RequireEqual(observed, expected);
                await _authority.ReverifyAsync(CancellationToken.None).ConfigureAwait(false);
                return observed;
            }
            catch (OperationCanceledException) when (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                throw new InstallerStateUncertainException("installer.recovery.original_baseline_write_uncertain");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            if (entered) { _gate.Release(); }
        }
    }

    private async Task<WindowsMaintenanceOriginalBaseline?> ReadLockedAsync(CancellationToken cancellationToken)
    {
        await _readGuard.EnsureProtectedAsync(_root, cancellationToken).ConfigureAwait(false);
        if (!_isRootPresent()) { return null; }
        byte[]? bytes = await _files.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
        if (bytes is null) { return null; }
        try { return WindowsMaintenanceOriginalBaseline.Parse(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireEqual(WindowsMaintenanceOriginalBaseline baseline, ReadOnlySpan<byte> expected)
    {
        byte[] actual = baseline.Serialize();
        try
        {
            if (!expected.SequenceEqual(actual)) { throw new InstallerProtocolException("installer.recovery.original_baseline_conflict"); }
        }
        finally { CryptographicOperations.ZeroMemory(actual); }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        _gate.Dispose();
        (_writeGuard as IDisposable)?.Dispose();
        (_readGuard as IDisposable)?.Dispose();
    }

    private static bool IsRecoverable(Exception exception) => exception is not
        (OutOfMemoryException or StackOverflowException or AccessViolationException or AppDomainUnloadedException);
}
