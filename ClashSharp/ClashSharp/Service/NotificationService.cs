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
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ClashSharp.Service;

/// <summary>Win11 notification gateway with policy filtering.</summary>
internal sealed class NotificationService
{
    public static NotificationService Instance { get; } = new(
        () => AppSettingsService.Instance.NotificationEnabled,
        () => AppSettingsService.Instance.NotificationLevel,
        LocalizationService.Instance.GetString,
        LogStorageService.Instance.AppendLog);

    private readonly Func<bool> _getEnabled;
    private readonly Func<NotificationLevel> _getLevel;
    private readonly Func<string, string> _getString;
    private readonly Action<string, string, string, string?> _appendLog;

    internal NotificationService(
        Func<bool> getEnabled,
        Func<NotificationLevel> getLevel,
        Func<string, string> getString,
        Action<string, string, string, string?> appendLog)
    {
        _getEnabled = getEnabled ?? throw new ArgumentNullException(nameof(getEnabled));
        _getLevel = getLevel ?? throw new ArgumentNullException(nameof(getLevel));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
    }

    public event EventHandler<NotificationRaisedEventArgs>? NotificationRaised;

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
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            AppendNotificationLog("Info", GetString("Notification.Log.Shown"), title, message);
            NotificationRaised?.Invoke(this, new NotificationRaisedEventArgs(minimumLevel, title, message));
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
        _appendLog(level, "Notification", message, null);
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

internal sealed record NotificationRaisedEventArgs(NotificationLevel Level, string Title, string Message);
