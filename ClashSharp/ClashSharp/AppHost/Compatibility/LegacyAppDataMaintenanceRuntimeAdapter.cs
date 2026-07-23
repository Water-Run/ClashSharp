using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Temporary Phase 05 bridge for destructive data maintenance runtime cleanup.</summary>
internal sealed class LegacyAppDataMaintenanceRuntimeAdapter(
    ConnectionSamplingService sampling,
    MihomoCoreService core,
    AppSettingsService settings,
    WindowsProxyService windowsProxy,
    LogStorageService logStorage,
    Func<string, string> getString) : IAppDataMaintenanceRuntime
{
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await TryCleanupAsync(() => sampling.StopAsync(cancellationToken));
        TryCleanup(core.Stop);
        if (settings.RestoreProxyOnExit)
        {
            TryCleanup(windowsProxy.DisableProxy);
        }
    }

    private async Task TryCleanupAsync(Func<Task> cleanup)
    {
        try
        {
            await cleanup();
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogCleanupFailure(exception);
        }
    }

    private void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception) when (exception is
            InvalidOperationException or Win32Exception or UnauthorizedAccessException)
        {
            LogCleanupFailure(exception);
        }
    }

    private void LogCleanupFailure(Exception exception)
    {
        logStorage.AppendLog(
            "Warning",
            "Maintenance",
            getString("Maintenance.RuntimeCleanupFailed"),
            exception.Message);
    }
}
