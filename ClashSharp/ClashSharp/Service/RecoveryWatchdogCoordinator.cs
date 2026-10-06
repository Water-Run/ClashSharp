using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Recovery;

namespace ClashSharp.Service;

/// <summary>Owns the per-user recovery lock and one watchdog lease for the primary GUI process.</summary>
/// <remarks>
/// The lock remains open for the primary process lifetime. A clean shutdown revokes the exact
/// lease before releasing the lock; an abrupt process exit leaves the lease for the helper.
/// </remarks>
internal sealed class RecoveryWatchdogCoordinator : IDisposable
{
    private const string HelperRelativePath = "ClashSharp.RecoveryWatchdog.exe";

    private readonly FileStream _installerMutationLock;
    private readonly FileStream _recoveryLock;
    private readonly RecoveryWatchdogLeaseFileStore _leaseStore;
    private readonly Action _restoreOwnedProxy;
    private RecoveryWatchdogLease? _lease;
    private bool _disposed;

    internal RecoveryWatchdogCoordinator(
        FileStream installerMutationLock,
        FileStream recoveryLock,
        RecoveryWatchdogLeaseFileStore leaseStore,
        Action restoreOwnedProxy)
    {
        _installerMutationLock = installerMutationLock ?? throw new ArgumentNullException(nameof(installerMutationLock));
        _recoveryLock = recoveryLock ?? throw new ArgumentNullException(nameof(recoveryLock));
        _leaseStore = leaseStore ?? throw new ArgumentNullException(nameof(leaseStore));
        _restoreOwnedProxy = restoreOwnedProxy ?? throw new ArgumentNullException(nameof(restoreOwnedProxy));
    }

    /// <summary>Returns no coordinator immediately when Installer ownership is unavailable.</summary>
    internal static async Task<RecoveryWatchdogCoordinator?> AcquireAsync(
        CancellationToken cancellationToken)
    {
        FileStream? installerMutationLock = await RecoveryWatchdogFileLock.TryAcquireAsync(
            RecoveryWatchdogPaths.ResolveInstallerMutationLockPath(),
            TimeSpan.Zero,
            cancellationToken).ConfigureAwait(false);
        if (installerMutationLock is null)
        {
            return null;
        }

        string localData = RecoveryWatchdogPaths.ResolveLocalDataDirectory();
        FileStream? recoveryLock;
        try
        {
            recoveryLock = await RecoveryWatchdogFileLock.TryAcquireAsync(
                Path.Combine(localData, RecoveryWatchdogPaths.LockFileName),
                TimeSpan.FromSeconds(10),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            installerMutationLock.Dispose();
            throw;
        }

        if (recoveryLock is null)
        {
            installerMutationLock.Dispose();
            throw new IOException("Timed out waiting for per-user proxy recovery ownership.");
        }

        return new RecoveryWatchdogCoordinator(
            installerMutationLock,
            recoveryLock,
            new RecoveryWatchdogLeaseFileStore(
                Path.Combine(localData, RecoveryWatchdogPaths.LeaseFileName)),
            () => WindowsProxyService.Instance.RestoreOwnedProxy());
    }

    /// <summary>Arms and launches the non-elevated one-shot helper for this exact process creation.</summary>
    /// <returns>True when the helper was launched; false when packaging or process launch failed.</returns>
    internal bool TryArm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lease is not null)
        {
            return true;
        }

        string helperPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, HelperRelativePath));
        if (!File.Exists(helperPath))
        {
            return false;
        }

        using Process current = Process.GetCurrentProcess();
        RecoveryWatchdogInvocation invocation = new(
            Guid.NewGuid(),
            Environment.ProcessId,
            current.StartTime.ToUniversalTime().Ticks);
        RecoveryWatchdogLease lease = invocation.ToLease();
        _leaseStore.Write(lease);
        try
        {
            ProcessStartInfo startInfo = new()
            {
                FileName = helperPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = AppContext.BaseDirectory,
            };
            invocation.AddTo(startInfo);
            using Process? helper = Process.Start(startInfo);
            if (helper is null)
            {
                _leaseStore.ClearIfMatches(lease);
                return false;
            }

            _lease = lease;
            return true;
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or Win32Exception)
        {
            _leaseStore.ClearIfMatches(lease);
            Debug.WriteLine($"ClashSharp recovery watchdog could not start ({exception.GetType().FullName}).");
            return false;
        }
    }

    /// <summary>Revokes this exact lease so a normal parent exit is a watchdog no-op.</summary>
    internal void Disarm()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lease is not { } lease)
        {
            return;
        }

        _leaseStore.ClearIfMatches(lease);
        _lease = null;
    }

    /// <summary>Quiesces failed startup and restores its proven proxy while the diagnostic window retains recovery ownership.</summary>
    /// <param name="stopRuntime">Stops and disposes host-owned work before proxy restoration.</param>
    /// <remarks>Neither success nor failure disarms the watchdog or releases process locks. Closing the window completes retirement.</remarks>
    internal async Task PrepareStartupFailureAsync(Func<Task> stopRuntime)
    {
        ArgumentNullException.ThrowIfNull(stopRuntime);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await stopRuntime().ConfigureAwait(false);
        _restoreOwnedProxy();
    }

    /// <summary>Retires failed-startup proxy ownership before revoking emergency recovery and releasing its locks.</summary>
    /// <remarks>
    /// The caller has finished host shutdown. A failed startup may precede network cleanup
    /// registration, so only the independent durable proxy journal can prove restoration.
    /// Failure preserves locks and the lease for bounded retry or the emergency helper.
    /// Successful ordinary startup retains its configured shutdown policy.
    /// </remarks>
    internal void CompleteNormalExit(bool startupFailed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (startupFailed)
        {
            _restoreOwnedProxy();
        }
        Disarm();
        Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _recoveryLock.Dispose();
        _installerMutationLock.Dispose();
        _disposed = true;
    }
}
