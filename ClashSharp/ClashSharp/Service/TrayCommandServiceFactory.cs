/*
 * Tray Command Service Factory
 * Wires tray command coordination to application singleton services
 *
 * @author: WaterRun
 * @file: Service/TrayCommandServiceFactory.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Creates application-wired tray command services.</summary>
internal static class TrayCommandServiceFactory
{
    public static TrayCommandService CreateDefault()
    {
        return new TrayCommandService(
            new TrayCommandSettingsAdapter(AppSettingsService.Instance),
            new TrayCommandMihomoServiceAdapter(MihomoServiceManager.Instance),
            new TrayCommandTakeoverAdapter(NetworkTakeoverService.Instance),
            new TrayCommandLogAdapter(LogStorageService.Instance));
    }
}

/// <summary>Adapts application settings to tray command settings.</summary>
internal sealed class TrayCommandSettingsAdapter(AppSettingsService settings) : ITrayCommandSettings
{
    public ClashSharpMode CurrentMode
    {
        get => settings.CurrentMode;
        set => settings.CurrentMode = value;
    }

    public bool TransparentProxyEnabled
    {
        get => settings.TransparentProxyEnabled;
        set => settings.TransparentProxyEnabled = value;
    }
}

/// <summary>Adapts mihomo service manager to tray command service status.</summary>
internal sealed class TrayCommandMihomoServiceAdapter(MihomoServiceManager serviceManager) : ITrayCommandMihomoService
{
    public MihomoServiceStatus GetStatus()
    {
        return serviceManager.GetStatus();
    }
}

/// <summary>Adapts network takeover service to tray commands.</summary>
internal sealed class TrayCommandTakeoverAdapter(NetworkTakeoverService takeover) : ITrayCommandTakeover
{
    public NetworkTakeoverResult ApplyMode(ClashSharpMode mode)
    {
        return takeover.ApplyMode(mode);
    }
}

/// <summary>Adapts log storage to tray command logging.</summary>
internal sealed class TrayCommandLogAdapter(LogStorageService log) : ITrayCommandLog
{
    public void Append(string level, string category, string message, string? detail)
    {
        log.AppendLog(level, category, message, detail);
    }
}
