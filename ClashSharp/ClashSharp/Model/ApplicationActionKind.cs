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
