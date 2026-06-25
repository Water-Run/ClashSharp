/*
 * Tray Status Service Factory
 * Wires tray status snapshots to runtime proxy state and node health storage
 *
 * @author: WaterRun
 * @file: Service/TrayStatusServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.Collections.Generic;
using System.Threading;
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

    public IReadOnlyList<MihomoProxyGroup> GetProxyGroups()
    {
        using CancellationTokenSource cancellation = new(RuntimeStatusTimeout);
        return controllerClient.GetProxyGroupsAsync(cancellation.Token).GetAwaiter().GetResult();
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
