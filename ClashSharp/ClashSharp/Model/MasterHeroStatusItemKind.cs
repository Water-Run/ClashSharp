/*
 * Master Hero Status Item Kind
 * Identifies compact status values that can be shown on the master-control hero card
 *
 * @author: WaterRun
 * @file: Model/MasterHeroStatusItemKind.cs
 * @date: 2026-06-29
 */

namespace ClashSharp.Model;

/// <summary>Status item kinds available for the master-control hero card.</summary>
internal enum MasterHeroStatusItemKind
{
    CoreStatus,
    SystemProxy,
    TransparentProxy,
    CurrentNode,
    Latency,
    UploadRate,
    DownloadRate,
    TotalTraffic,
    ActiveConnections,
    CurrentMode,
    ActiveProfile,
    MihomoService,
    StartupLaunch,
    Availability,
}
