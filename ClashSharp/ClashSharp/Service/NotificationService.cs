/*
 * Notification Service
 * Sends Win11 system notifications according to the configured notification policy
 *
 * @author: WaterRun
 * @file: Service/NotificationService.cs
 * @date: 2026-06-26
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using Windows.Storage;
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
    /// <summary>Checks whether a notification with the stable trigger identity is still registered.</summary>
    Task<bool> ContainsAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Shows one Win11 notification.</summary>
    void Show(string title, string message, string? idempotencyKey = null);
}

/// <summary>Durable receipt boundary used to bridge notification display and outbox commit.</summary>
internal interface ITriggerNotificationReceiptStore
{
    bool Contains(string idempotencyKey);

    void Record(string idempotencyKey);
}

/// <summary>Idempotent notification operations required by the durable trigger runtime.</summary>
internal interface IIdempotentTriggerNotificationSink
{
    Task<bool> IsTriggerNotificationDeliveredAsync(
        string idempotencyKey,
        CancellationToken cancellationToken);

    Task DeliverTriggerNotificationAsync(
        string idempotencyKey,
        string message,
        CancellationToken cancellationToken);
}

#if !UNIT_TESTS
/// <summary>Default Win11 notification platform backed by Windows App SDK notifications.</summary>
internal sealed class Win11NotificationPlatform : IWin11NotificationPlatform
{
    public static Win11NotificationPlatform Instance { get; } = new();

    private Win11NotificationPlatform()
    {
    }

    public async Task<bool> ContainsAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string tag, string group) = CreateIdentity(idempotencyKey);
        IList<AppNotification> notifications = await AppNotificationManager.Default.GetAllAsync();
        cancellationToken.ThrowIfCancellationRequested();
        return notifications.Any(notification =>
            StringComparer.Ordinal.Equals(notification.Tag, tag)
            && StringComparer.Ordinal.Equals(notification.Group, group));
    }

    public void Show(string title, string message, string? idempotencyKey = null)
    {
        AppNotification notification = new AppNotificationBuilder()
            .AddText(title)
            .AddText(message)
            .BuildNotification();
        if (idempotencyKey is not null)
        {
            (notification.Tag, notification.Group) = CreateIdentity(idempotencyKey);
        }

        AppNotificationManager.Default.Show(notification);
    }

    private static (string Tag, string Group) CreateIdentity(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))
            .ToLowerInvariant();
        return (hash[..16], hash[16..32]);
    }
}
#endif

/// <summary>Win11 notification gateway with policy filtering.</summary>
internal sealed class NotificationService :
    ITriggerNotificationSink,
    IApplicationNotificationSink,
    IIdempotentTriggerNotificationSink
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
    private readonly ITriggerNotificationReceiptStore _triggerReceipts;

    internal NotificationService(
        Func<bool> getEnabled,
        Func<NotificationLevel> getLevel,
        Func<string, string> getString,
        Action<string, string, string, string?> appendLog,
        ITriggerRuntimeEventPublisher triggerEvents,
        IWin11NotificationPlatform platform,
        ITriggerNotificationReceiptStore? triggerReceipts = null)
    {
        _getEnabled = getEnabled ?? throw new ArgumentNullException(nameof(getEnabled));
        _getLevel = getLevel ?? throw new ArgumentNullException(nameof(getLevel));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _triggerEvents = triggerEvents ?? throw new ArgumentNullException(nameof(triggerEvents));
        _platform = platform ?? throw new ArgumentNullException(nameof(platform));
        _triggerReceipts = triggerReceipts ?? new LocalTriggerNotificationReceiptStore();
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

    public async Task<bool> IsTriggerNotificationDeliveredAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (_triggerReceipts.Contains(idempotencyKey))
        {
            return true;
        }

        bool registered = await _platform.ContainsAsync(idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (registered)
        {
            _triggerReceipts.Record(idempotencyKey);
        }

        return registered;
    }

    public async Task DeliverTriggerNotificationAsync(
        string idempotencyKey,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (await IsTriggerNotificationDeliveredAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string title = GetString("Notification.Custom.Title");
        string detail = string.IsNullOrWhiteSpace(message)
            ? GetString("Notification.Custom.Message")
            : message.Trim();
        if (!ShouldShow(NotificationLevel.Default))
        {
            AppendNotificationLog("Info", GetString("Notification.Log.Suppressed"), title, detail);
            _triggerReceipts.Record(idempotencyKey);
            return;
        }

        try
        {
            _platform.Show(title, detail, idempotencyKey);
            _triggerReceipts.Record(idempotencyKey);
            AppendNotificationLog("Info", GetString("Notification.Log.Shown"), title, detail);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppendNotificationLog("Warning", GetString("Notification.Log.Failed"), title, detail, exception.Message);
            throw;
        }
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
    public Task<bool> ContainsAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }

    public void Show(string title, string message, string? idempotencyKey = null)
    {
        throw new NotSupportedException("Tests must inject a notification platform.");
    }
}
#endif

/// <summary>Persists bounded recent notification receipts in Windows local settings.</summary>
internal sealed class LocalTriggerNotificationReceiptStore : ITriggerNotificationReceiptStore
{
    private const string KeyPrefix = "TriggerNotificationReceiptV1.";
    private const int MaximumReceipts = 2048;
    private const int PruneCount = 256;
    private readonly object _syncLock = new();
    private readonly Dictionary<string, long> _fallback = new(StringComparer.Ordinal);
    private readonly ApplicationDataContainer? _settings;

    public LocalTriggerNotificationReceiptStore()
    {
        try
        {
            _settings = ApplicationData.Current.LocalSettings;
        }
        catch (InvalidOperationException)
        {
            _settings = null;
        }
    }

    public bool Contains(string idempotencyKey)
    {
        string key = CreateStorageKey(idempotencyKey);
        lock (_syncLock)
        {
            return _settings?.Values.ContainsKey(key) ?? _fallback.ContainsKey(key);
        }
    }

    public void Record(string idempotencyKey)
    {
        string key = CreateStorageKey(idempotencyKey);
        long timestamp = DateTimeOffset.UtcNow.UtcTicks;
        lock (_syncLock)
        {
            if (_settings is null)
            {
                _fallback[key] = timestamp;
                PruneFallback();
                return;
            }

            _settings.Values[key] = timestamp;
            List<(string Key, long Timestamp)> receipts = _settings.Values
                .Where(pair => pair.Key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                .Select(pair => (pair.Key, pair.Value is long value ? value : 0L))
                .OrderBy(static pair => pair.Item2)
                .ToList();
            int removalCount = receipts.Count > MaximumReceipts
                ? receipts.Count - MaximumReceipts + PruneCount
                : 0;
            foreach ((string staleKey, _) in receipts.Take(removalCount))
            {
                _settings.Values.Remove(staleKey);
            }
        }
    }

    private void PruneFallback()
    {
        int removalCount = _fallback.Count > MaximumReceipts
            ? _fallback.Count - MaximumReceipts + PruneCount
            : 0;
        foreach (string staleKey in _fallback
                     .OrderBy(static pair => pair.Value)
                     .Take(removalCount)
                     .Select(static pair => pair.Key)
                     .ToArray())
        {
            _fallback.Remove(staleKey);
        }
    }

    private static string CreateStorageKey(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))
            .ToLowerInvariant();
        return KeyPrefix + hash;
    }
}
