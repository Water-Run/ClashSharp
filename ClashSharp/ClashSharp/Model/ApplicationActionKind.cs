/*
 * Application Action Kind
 * Shared action identifiers for tiles, triggers, and traditional UI commands
 *
 * @author: WaterRun
 * @file: Model/ApplicationActionKind.cs
 * @date: 2026-06-26
 */

namespace ClashSharp.Model;

/// <summary>Shared application-level action identifiers used across UI entry points.</summary>
internal enum ApplicationActionKind
{
    ExportConfiguration,
    ImportConfiguration,
    SetLaunchAtStartup,
    SetTransparentProxy,
    SetConnectionSampling,
    SwitchProxyMode,
    CloseConnections,
    ExitApplication,
    SendNotification,
}
