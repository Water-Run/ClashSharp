/*
 * Application Action Service
 * Coordinates shared application actions requested by tiles, triggers, and traditional UI entry points
 *
 * @author: WaterRun
 * @file: Service/ApplicationActionService.cs
 * @date: 2026-06-26
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Default shared dispatcher for non-picker application actions.</summary>
internal sealed class ApplicationActionService : IApplicationActionDispatcher
{
    public static ApplicationActionService Instance { get; } = new(
        AppSettingsService.Instance,
        NetworkTakeoverService.Instance,
        MihomoConnectionService.Instance,
        NotificationService.Instance,
        LogStorageService.Instance.AppendLog,
        LocalizationService.Instance.GetString,
        () => App.MainWindow?.Close());

    private readonly AppSettingsService _settings;
    private readonly NetworkTakeoverService _takeover;
    private readonly MihomoConnectionService _connections;
    private readonly NotificationService _notifications;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;
    private readonly Action _exitApplication;

    internal ApplicationActionService(
        AppSettingsService settings,
        NetworkTakeoverService takeover,
        MihomoConnectionService connections,
        NotificationService notifications,
        Action<string, string, string, string?> appendLog,
        Func<string, string> getString,
        Action exitApplication)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _takeover = takeover ?? throw new ArgumentNullException(nameof(takeover));
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _exitApplication = exitApplication ?? throw new ArgumentNullException(nameof(exitApplication));
    }

    public async Task DispatchAsync(ApplicationActionKind kind, string value, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case ApplicationActionKind.SetLaunchAtStartup:
                bool launchAtStartup = ParseBoolean(value);
                _settings.LaunchAtStartupEnabled = launchAtStartup;
                await StartupLaunchService.Instance.SetEnabledAsync(launchAtStartup).ConfigureAwait(false);
                break;
            case ApplicationActionKind.SetTransparentProxy:
                _settings.TransparentProxyEnabled = ParseBoolean(value);
                break;
            case ApplicationActionKind.SetConnectionSampling:
                _settings.ConnectionSamplingEnabled = ParseBoolean(value);
                ConnectionSamplingService.Instance.RestartFromSettings();
                break;
            case ApplicationActionKind.SwitchProxyMode:
                ClashSharpMode mode = Enum.TryParse(value, out ClashSharpMode parsedMode) ? parsedMode : _settings.CurrentMode;
                NetworkTakeoverResult result = _takeover.ApplyMode(mode);
                _settings.CurrentMode = result.Mode;
                _notifications.NotifyProxyModeChanged(result.Mode);
                if (result.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover)
                {
                    await TriggerService.Instance.EvaluateAsync(
                        TriggerEvaluationContextFactory.Create(TriggerEventKind.ProxyStarted),
                        cancellationToken).ConfigureAwait(false);
                }
                break;
            case ApplicationActionKind.CloseConnections:
                await _connections.CloseAllConnectionsAsync(cancellationToken).ConfigureAwait(false);
                break;
            case ApplicationActionKind.SendNotification:
                _notifications.NotifyCustom(value);
                break;
            case ApplicationActionKind.ExitApplication:
                _exitApplication();
                break;
            case ApplicationActionKind.ExportConfiguration:
            case ApplicationActionKind.ImportConfiguration:
                _appendLog(
                    "Info",
                    "ApplicationAction",
                    string.Format(_getString("ApplicationAction.UiPickerRequired.Format"), kind),
                    value);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unsupported application action.");
        }
    }

    private static bool ParseBoolean(string value)
    {
        return bool.TryParse(value, out bool parsed) && parsed;
    }
}
