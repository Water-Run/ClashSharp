using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class TrayStatusService
{
    /// <summary>Shared tray status service instance.</summary>
    public static TrayStatusService Instance { get; } = TrayStatusServiceFactory.CreateDefault();
}

/// <summary>Creates application-wired tray status services.</summary>
internal static class TrayStatusServiceFactory
{
    public static TrayStatusService CreateDefault()
    {
        return new TrayStatusService(
            new TrayStatusRuntimeAdapter(
                MihomoControllerClient.Instance.GetProxyGroupsAsync,
                CoreConfigurationService.Instance.ObserveRuntimeConfigurationIntegrity),
            new TrayStatusHealthStorageAdapter(LogStorageService.Instance),
            MainlandChinaTextDisplayService.Instance.Apply);
    }
}

/// <summary>Adapts log storage node health rows to tray status latency data.</summary>
internal sealed class TrayStatusHealthStorageAdapter(LogStorageService logStorage) : ITrayStatusHealthStorage
{
    public int? GetNodeLatencyMilliseconds(string nodeName)
    {
        return logStorage.GetNodeLatencyMilliseconds(nodeName);
    }
}
