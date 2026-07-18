/*
 * Tray Command Service Factory
 * Wires tray command coordination to application singleton services
 *
 * @author: WaterRun
 * @file: Service/TrayCommandServiceFactory.cs
 * @date: 2026-06-25
 */

using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Creates application-wired tray command services.</summary>
internal static class TrayCommandServiceFactory
{
    public static TrayCommandService CreateDefault(ApplicationActionService actions)
    {
        return new TrayCommandService(
            new TrayCommandSettingsAdapter(AppSettingsService.Instance),
            new TrayCommandTakeoverAdapter(actions),
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

/// <summary>Adapts network takeover service to tray commands.</summary>
internal sealed class TrayCommandTakeoverAdapter(ApplicationActionService actions) : ITrayCommandTakeover
{
    public Task<NetworkTakeoverResult> ApplyModeAsync(
        ClashSharpMode mode,
        CancellationToken cancellationToken)
    {
        return actions.ApplyNetworkModeAsync(mode, cancellationToken);
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
