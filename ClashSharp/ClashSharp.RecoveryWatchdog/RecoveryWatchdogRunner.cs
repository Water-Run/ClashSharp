using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ClashSharp.Service;

namespace ClashSharp.Recovery;

internal sealed class RecoveryWatchdogRunner(
    Func<RecoveryWatchdogInvocation, CancellationToken, Task> waitForParentExit,
    Func<CancellationToken, Task<IDisposable?>> acquireRecoveryLock,
    Func<RecoveryWatchdogLease?> readLease,
    Action<RecoveryWatchdogLease> clearLeaseIfMatches,
    Func<bool> restoreOwnedProxy)
{
    internal async Task<int> RunAsync(
        RecoveryWatchdogInvocation invocation,
        CancellationToken cancellationToken)
    {
        RecoveryWatchdogLease expected = invocation.ToLease();
        if (readLease() != expected)
        {
            return 0;
        }

        await waitForParentExit(invocation, cancellationToken).ConfigureAwait(false);
        using IDisposable? recoveryLock = await acquireRecoveryLock(cancellationToken).ConfigureAwait(false);
        if (recoveryLock is null || readLease() != expected)
        {
            return 0;
        }

        bool restored = restoreOwnedProxy();
        clearLeaseIfMatches(expected);
        return restored ? 1 : 0;
    }

    internal static async Task WaitForParentExitAsync(
        RecoveryWatchdogInvocation invocation,
        CancellationToken cancellationToken)
    {
        Process? parent = null;
        try
        {
            parent = Process.GetProcessById(invocation.ParentProcessId);
            long actualStartTime = parent.StartTime.ToUniversalTime().Ticks;
            if (actualStartTime != invocation.ParentStartTimeUtcTicks)
            {
                return;
            }

            await parent.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ArgumentException)
        {
            // The exact parent already exited before the helper attached.
        }
        catch (InvalidOperationException)
        {
            // The process exited while its creation identity was being queried.
        }
        finally
        {
            parent?.Dispose();
        }
    }

    internal static bool RestoreOwnedProxy(
        IWindowsProxyRegistryStore registry,
        IWindowsProxyMutationJournalStore journal)
    {
        return WindowsProxyOwnershipRestorer.Restore(registry, journal);
    }

    internal static void NotifyProxySettingsChanged()
    {
        const int internetOptionSettingsChanged = 39;
        const int internetOptionRefresh = 37;
        if (!InternetSetOption(nint.Zero, internetOptionSettingsChanged, nint.Zero, 0)
            || !InternetSetOption(nint.Zero, internetOptionRefresh, nint.Zero, 0))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    [DllImport("wininet.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern bool InternetSetOption(nint internet, int option, nint buffer, int bufferLength);
}
