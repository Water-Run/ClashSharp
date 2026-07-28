using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
            new TrayStatusRuntimeAdapter(MihomoControllerClient.Instance),
            new TrayStatusHealthStorageAdapter(LogStorageService.Instance),
            MainlandChinaTextDisplayService.Instance.Apply);
    }
}

/// <summary>Adapts mihomo controller state to tray status runtime data.</summary>
internal sealed class TrayStatusRuntimeAdapter(MihomoControllerClient controllerClient) : ITrayStatusRuntime
{
    private static readonly TimeSpan RuntimeStatusTimeout = TimeSpan.FromMilliseconds(800);

    public async Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = new(RuntimeStatusTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        return await controllerClient.GetProxyGroupsAsync(linked.Token).ConfigureAwait(false);
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
