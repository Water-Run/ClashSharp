using System;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Temporary Phase 05 bridge for destructive data maintenance runtime cleanup.</summary>
internal sealed class LegacyAppDataMaintenanceRuntimeAdapter(
    ConnectionSamplingService sampling,
    Func<CancellationToken, Task> stopNetworkRuntimeAsync,
    LogStorageService logStorage,
    Func<string, string> getString) : IAppDataMaintenanceRuntime
{
    public async Task ShutdownAsync(CancellationToken cancellationToken)
    {
        await TryCleanupAsync(() => sampling.StopAsync(cancellationToken));
        await stopNetworkRuntimeAsync(cancellationToken).ConfigureAwait(false);
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

    private void LogCleanupFailure(Exception exception)
    {
        logStorage.AppendLog(
            "Warning",
            "Maintenance",
            getString("Maintenance.RuntimeCleanupFailed"),
            exception.Message);
    }
}
