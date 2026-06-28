/*
 * Notification Service
 * Sends Win11 system notifications according to the configured notification policy
 *
 * @author: WaterRun
 * @file: Service/NotificationService.cs
 * @date: 2026-06-26
 */

using System;
using System.Globalization;
using ClashSharp.Model;
#if !UNIT_TESTS
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;
#endif

namespace ClashSharp.Service;

/// <summary>Notification commands needed by application actions.</summary>
internal interface IApplicationNotificationSink
{
    /// <summary>Sends a notification after the proxy mode changes.</summary>
    void NotifyProxyModeChanged(ClashSharpMode mode);

    /// <summary>Sends a custom user-visible notification.</summary>
    void NotifyCustom(string message);
}

/// <summary>Win11 notification display boundary used by <see cref="NotificationService"/>.</summary>
internal interface IWin11NotificationPlatform
{
    /// <summary>Shows one Win11 notification.</summary>
    void Show(string title, string message);
}

#if !UNIT_TESTS
/// <summary>Default Win11 notification platform backed by Windows App SDK notifications.</summary>
internal sealed class Win11NotificationPlatform : IWin11NotificationPlatform
{
    public static Win11NotificationPlatform Instance { get; } = new();

    private Win11NotificationPlatform()
    {
    }

    public void Show(string title, string message)
    {
        AppNotification notification = new AppNotificationBuilder()
            .AddText(title)
            .AddText(message)
            .BuildNotification();
        AppNotificationManager.Default.Show(notification);
    }
}
#endif

/// <summary>Win11 notification gateway with policy filtering.</summary>
internal sealed class NotificationService : ITriggerNotificationSink, IApplicationNotificationSink
{
    public static NotificationService Instance { get; } = new(
        () => AppSettingsService.Instance.NotificationEnabled,
        () => AppSettingsService.Instance.NotificationLevel,
        LocalizationService.Instance.GetString,
        LogStorageService.Instance.AppendLog,
        TriggerRuntimeEventHub.Instance,
#if UNIT_TESTS
        new ThrowingTestNotificationPlatform());
#else
        Win11NotificationPlatform.Instance);
#endif

    private readonly Func<bool> _getEnabled;
    private readonly Func<NotificationLevel> _getLevel;
    private readonly Func<string, string> _getString;
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly ITriggerRuntimeEventPublisher _triggerEvents;
    private readonly IWin11NotificationPlatform _platform;

    internal NotificationService(
        Func<bool> getEnabled,
        Func<NotificationLevel> getLevel,
        Func<string, string> getString,
        Action<string, string, string, string?> appendLog,
        ITriggerRuntimeEventPublisher triggerEvents,
        IWin11NotificationPlatform platform)
    {
        _getEnabled = getEnabled ?? throw new ArgumentNullException(nameof(getEnabled));
        _getLevel = getLevel ?? throw new ArgumentNullException(nameof(getLevel));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _triggerEvents = triggerEvents ?? throw new ArgumentNullException(nameof(triggerEvents));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
    }

    public void NotifyProxyModeChanged(ClashSharpMode mode)
    {
        Show(
            NotificationLevel.Default,
            GetString("Notification.ProxyMode.Title"),
            string.Format(CultureInfo.CurrentCulture, GetString("Notification.ProxyMode.Message.Format"), GetModeLabel(mode)));
    }

    public void NotifyTriggerFired(string triggerName)
    {
        Show(
            NotificationLevel.Default,
            GetString("Notification.TriggerFired.Title"),
            string.Format(CultureInfo.CurrentCulture, GetString("Notification.TriggerFired.Message.Format"), triggerName));
    }

    public void NotifyConnectionTestTimeout(string target)
    {
        Show(
            NotificationLevel.CriticalOnly,
            GetString("Notification.ConnectionTestTimeout.Title"),
            string.Format(CultureInfo.CurrentCulture, GetString("Notification.ConnectionTestTimeout.Message.Format"), target));
    }

    public void NotifyCustom(string message)
    {
        Show(
            NotificationLevel.Default,
            GetString("Notification.Custom.Title"),
            string.IsNullOrWhiteSpace(message) ? GetString("Notification.Custom.Message") : message.Trim());
    }

    public void Show(NotificationLevel minimumLevel, string title, string message)
    {
        if (!ShouldShow(minimumLevel))
        {
            AppendNotificationLog("Info", GetString("Notification.Log.Suppressed"), title, message);
            return;
        }

        try
        {
            _platform.Show(title, message);
            AppendNotificationLog("Info", GetString("Notification.Log.Shown"), title, message);
            _triggerEvents.Publish(new TriggerRuntimeEvent(TriggerEventKind.NotificationRaised, minimumLevel));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            AppendNotificationLog("Warning", GetString("Notification.Log.Failed"), title, message, exception.Message);
        }
    }

    private bool ShouldShow(NotificationLevel minimumLevel)
    {
        if (!_getEnabled())
        {
            return false;
        }

        NotificationLevel configured = _getLevel();
        return configured switch
        {
            NotificationLevel.CriticalOnly => minimumLevel == NotificationLevel.CriticalOnly,
            NotificationLevel.More => true,
            _ => minimumLevel is NotificationLevel.Default or NotificationLevel.CriticalOnly,
        };
    }

    private void AppendNotificationLog(string level, string messageTemplate, string title, string detail, string? error = null)
    {
        string message = error is null
            ? string.Format(CultureInfo.CurrentCulture, messageTemplate, title, detail)
            : string.Format(CultureInfo.CurrentCulture, messageTemplate, title, detail, error);
        _appendLog(level, "Notification", message, BuildNotificationDetail(title, detail, error));
    }

    private static string BuildNotificationDetail(string title, string message, string? error)
    {
        return error is null
            ? $"Title: {title}{Environment.NewLine}Message: {message}"
            : $"Title: {title}{Environment.NewLine}Message: {message}{Environment.NewLine}Error: {error}";
    }

    private string GetModeLabel(ClashSharpMode mode)
    {
        return mode switch
        {
            ClashSharpMode.Standby => GetString("Master.Mode.Standby.Title"),
            ClashSharpMode.RuleTakeover => GetString("Master.Mode.RuleTakeover.Title"),
            ClashSharpMode.FullTakeover => GetString("Master.Mode.FullTakeover.Title"),
            _ => GetString("Master.Mode.Disabled.Title"),
        };
    }

    private string GetString(string key)
    {
        return _getString(key);
    }
}

#if UNIT_TESTS
internal sealed class ThrowingTestNotificationPlatform : IWin11NotificationPlatform
{
    public void Show(string title, string message)
    {
        throw new NotSupportedException("Tests must inject a notification platform.");
    }
}
#endif
