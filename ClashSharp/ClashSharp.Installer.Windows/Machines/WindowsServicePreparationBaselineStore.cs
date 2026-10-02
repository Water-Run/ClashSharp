using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Persists the first original-service observation in the authority-only recovery root. A global
/// authenticated recovery lease must outlive this store; the local gate is not cross-process
/// authority. No delete capability is exposed until verified recovery/finalization is integrated.
/// </summary>
internal sealed class WindowsServicePreparationBaselineStore : IDisposable
{
    private readonly string _root;
    private readonly string _path;
    private readonly IInstallerTransactionRootGuard _readGuard;
    private readonly IInstallerTransactionRootGuard _writeGuard;
    private readonly Func<bool> _isRootPresent;
    private readonly IWindowsInstallerPrivateJournalFileNative _files;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    internal WindowsServicePreparationBaselineStore(string root, IInstallerTransactionRootGuard readGuard,
        IInstallerTransactionRootGuard writeGuard, Func<bool> isRootPresent, IWindowsInstallerPrivateJournalFileNative files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(readGuard);
        ArgumentNullException.ThrowIfNull(writeGuard);
        ArgumentNullException.ThrowIfNull(isRootPresent);
        ArgumentNullException.ThrowIfNull(files);
        if (!Path.IsPathFullyQualified(root)) { throw new ArgumentException("The private recovery root must be absolute.", nameof(root)); }
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _path = Path.Combine(_root, WindowsServicePreparationBaseline.FileName);
        _readGuard = readGuard;
        _writeGuard = writeGuard;
        _isRootPresent = isRootPresent;
        _files = files;
    }

    internal static WindowsServicePreparationBaselineStore CreateDefault()
    {
        WindowsInstallerTransactionRootGuard read = WindowsInstallerTransactionRootGuard.CreateReadOnlyOwnerTransferDefault();
        WindowsInstallerTransactionRootGuard write = WindowsInstallerTransactionRootGuard.CreatePrivateOwnerTransferDefault();
        return new(read.RootPath, read, write, () => read.IsProtectedRootPresent,
            WindowsInstallerPrivateJournalFileNative.CreateForServicePreparationBaseline());
    }

    internal async Task<WindowsServicePreparationBaseline?> ReadAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await ReadLockedAsync(cancellationToken).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task<WindowsServicePreparationBaseline> CaptureOnceAsync(WindowsServicePreparationBaseline baseline,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(baseline);
        byte[] expected = baseline.Serialize();
        bool entered = false;
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            entered = true;
            WindowsServicePreparationBaseline? existing = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireEqual(existing, expected);
                return existing;
            }
            await _writeGuard.EnsureProtectedAsync(_root, cancellationToken).ConfigureAwait(false);
            existing = await ReadLockedAsync(cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                RequireEqual(existing, expected);
                return existing;
            }
            cancellationToken.ThrowIfCancellationRequested();
            Exception? failure = null;
            try { await _files.WriteAtomicallyAsync(_path, expected, cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (IsRecoverable(exception)) { failure = exception; }
            WindowsServicePreparationBaseline? observed;
            try { observed = await ReadLockedAsync(CancellationToken.None).ConfigureAwait(false); }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                throw new InstallerStateUncertainException("installer.recovery.service_baseline_write_uncertain");
            }
            if (observed is not null)
            {
                try { RequireEqual(observed, expected); }
                catch (InstallerProtocolException)
                {
                    throw new InstallerStateUncertainException("installer.recovery.service_baseline_write_uncertain");
                }
                return observed;
            }
            if (failure is OperationCanceledException && cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            throw new InstallerStateUncertainException("installer.recovery.service_baseline_write_uncertain");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expected);
            if (entered) { _gate.Release(); }
        }
    }

    private async Task<WindowsServicePreparationBaseline?> ReadLockedAsync(CancellationToken cancellationToken)
    {
        await _readGuard.EnsureProtectedAsync(_root, cancellationToken).ConfigureAwait(false);
        if (!_isRootPresent()) { return null; }
        byte[]? bytes = await _files.ReadAsync(_path, cancellationToken).ConfigureAwait(false);
        if (bytes is null) { return null; }
        try { return WindowsServicePreparationBaseline.Parse(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static void RequireEqual(WindowsServicePreparationBaseline baseline, ReadOnlySpan<byte> expected)
    {
        byte[] actual = baseline.Serialize();
        try
        {
            if (!expected.SequenceEqual(actual)) { throw new InstallerProtocolException("installer.recovery.service_baseline_conflict"); }
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
