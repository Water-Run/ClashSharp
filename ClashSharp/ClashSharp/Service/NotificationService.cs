/*
 * Notification Service
 * Sends Win11 system notifications according to the configured notification policy
 *
 * @author: WaterRun
 * @file: Service/NotificationService.cs
 * @date: 2026-06-26
 */

using System;
using ClashSharp.Model;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace ClashSharp.Service;

/// <summary>Win11 notification gateway with policy filtering.</summary>
internal sealed class NotificationService
{
    public static NotificationService Instance { get; } = new(
        () => AppSettingsService.Instance.NotificationLevel,
        LogStorageService.Instance.AppendLog);

    private readonly Func<NotificationLevel> _getLevel;
    private readonly Action<string, string, string, string?> _appendLog;

    internal NotificationService(Func<NotificationLevel> getLevel, Action<string, string, string, string?> appendLog)
    {
        _getLevel = getLevel ?? throw new ArgumentNullException(nameof(getLevel));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
    }

    public event EventHandler<NotificationRaisedEventArgs>? NotificationRaised;

    public void NotifyProxyModeChanged(ClashSharpMode mode)
    {
        Show(NotificationLevel.Default, "Clash# proxy mode", mode.ToString());
    }

    public void NotifyTriggerFired(string triggerName)
    {
        Show(NotificationLevel.Default, "Clash# trigger fired", triggerName);
    }

    public void NotifyConnectionTestTimeout(string target)
    {
        Show(NotificationLevel.CriticalOnly, "Clash# URL validation timed out", target);
    }

    public void Show(NotificationLevel minimumLevel, string title, string message)
    {
        if (!ShouldShow(minimumLevel))
        {
            return;
        }

        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();
            AppNotificationManager.Default.Show(notification);
            NotificationRaised?.Invoke(this, new NotificationRaisedEventArgs(minimumLevel, title, message));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            _appendLog("Warning", "Notification", "System notification could not be shown.", exception.Message);
        }
    }

    private bool ShouldShow(NotificationLevel minimumLevel)
    {
        NotificationLevel configured = _getLevel();
        return configured switch
        {
            NotificationLevel.CriticalOnly => minimumLevel == NotificationLevel.CriticalOnly,
            NotificationLevel.More => true,
            _ => minimumLevel is NotificationLevel.Default or NotificationLevel.CriticalOnly,
        };
    }
}

internal sealed record NotificationRaisedEventArgs(NotificationLevel Level, string Title, string Message);
