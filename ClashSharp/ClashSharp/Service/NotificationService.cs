using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;
using TriggerEventKind = global::ClashSharp.Model.Triggers.TriggerEventKind;
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

    /// <summary>Reports a failed proxy availability change.</summary>
    void NotifyProxyFailure() { }
}

/// <summary>Win11 notification display boundary used by <see cref="NotificationService"/>.</summary>
internal interface IWin11NotificationPlatform
{
    /// <summary>Checks whether a notification with the stable trigger identity is still registered.</summary>
    Task<bool> ContainsAsync(string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>Shows one Win11 notification.</summary>
    void Show(string title, string message, string? idempotencyKey = null);
}

/// <summary>Owns notification activation registration for one primary window lifetime.</summary>
internal interface IWin11NotificationRegistration
{
    IDisposable Register(Action activated);
}

/// <summary>Releases exactly one owned registration.</summary>
internal sealed class NotificationRegistration(Action release) : IDisposable
{
    private Action? _release = release;
    public void Dispose() => Interlocked.Exchange(ref _release, null)?.Invoke();
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
internal sealed class Win11NotificationPlatform : IWin11NotificationPlatform, IWin11NotificationRegistration, IDisposable
{
    private readonly object _registrationGate = new();
    private IDisposable? _registration;
    private Action? _activated;
    public static Win11NotificationPlatform Instance { get; } = new();

    private Win11NotificationPlatform()
    {
    }

    public IDisposable Register(Action activated)
    {
        ArgumentNullException.ThrowIfNull(activated);
        lock (_registrationGate)
        {
            _activated = activated;
            EnsureRegistration();
        }
        return new NotificationRegistration(Dispose);
    }

    private void OnInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args) =>
        Volatile.Read(ref _activated)?.Invoke();

    private void EnsureRegistration()
    {
        if (_registration is not null) { return; }
        AppNotificationManager manager = AppNotificationManager.Default;
        manager.NotificationInvoked += OnInvoked;
        try { manager.Register(); }
        catch { manager.NotificationInvoked -= OnInvoked; throw; }
        _registration = new NotificationRegistration(() =>
        {
            manager.NotificationInvoked -= OnInvoked;
            manager.Unregister();
        });
    }

    public void Dispose()
    {
        lock (_registrationGate)
        {
            _activated = null;
            IDisposable? registration = _registration;
            _registration = null;
            registration?.Dispose();
        }
    }

    public async Task<bool> ContainsAsync(
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_registrationGate) { EnsureRegistration(); }
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

        lock (_registrationGate)
        {
            EnsureRegistration();
            AppNotificationManager.Default.Show(notification);
            if (notification.Id == 0) { throw new InvalidOperationException("Windows did not accept the notification."); }
        }
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
    IApplicationNotificationSink,
    IUserOperationNotifications,
    IDisposable,
    IIdempotentTriggerNotificationSink
{
    public static NotificationService Instance { get; } = new(
        () => AppSettingsService.Instance.NotificationEnabled,
        () => AppSettingsService.Instance.NotificationLevel,
        LocalizationService.Instance.GetString,
        static (level, category, message, detail) => RuntimeDataServices.Logs.AppendLog(level, category, message, detail),
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
    private NotificationPolicySnapshot? _lastPolicy;

    private sealed record NotificationPolicySnapshot(bool Enabled, NotificationLevel Level);

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

    /// <summary>Uses detailed verbosity for success and the standard policy for actionable failures.</summary>
    public void ReportOperation(string titleResourceKey, string messageResourceKey, bool failed, params object[] arguments)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(arguments);
            Show(failed ? NotificationLevel.Default : NotificationLevel.More,
                GetString(titleResourceKey), arguments.Length == 0 ? GetString(messageResourceKey)
                    : string.Format(CultureInfo.CurrentCulture, GetString(messageResourceKey), arguments));
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            TryAppendNotificationLog("Warning", "Operation notification failed", titleResourceKey, messageResourceKey, exception.GetType().Name);
        }
    }

    public void ReportAvailability(string titleResourceKey, string messageResourceKey, bool failed) =>
        Show(failed ? NotificationLevel.CriticalOnly : NotificationLevel.Default, GetString(titleResourceKey), GetString(messageResourceKey));

    internal IDisposable Register(Action activated)
    {
        try
        {
            _ = ShouldShow(NotificationLevel.More);
            return _platform is IWin11NotificationRegistration registration
                ? registration.Register(activated) : new NotificationRegistration(static () => { });
        }
        catch (Exception exception) when (IsRecoverableBestEffortFailure(exception))
        {
            TryAppendNotificationLog("Warning", GetString("Notification.Log.Failed"), "Registration", "notification.registration.failed", exception.GetType().Name);
            return new NotificationRegistration(static () => { });
        }
    }

    public void Dispose()
    {
        try { if (_platform is IDisposable owned) { owned.Dispose(); } }
        catch (Exception exception) when (IsRecoverableBestEffortFailure(exception))
        {
            TryAppendNotificationLog("Warning", GetString("Notification.Log.Failed"), "Registration", "notification.unregister.failed", exception.GetType().Name);
        }
        Volatile.Write(ref _lastPolicy, null);
    }

    public void NotifyConnectionTestTimeout(string target)
    {
        Show(
            NotificationLevel.Default,
            GetString("Notification.ConnectionTestTimeout.Title"),
            string.Format(CultureInfo.CurrentCulture, GetString("Notification.ConnectionTestTimeout.Message.Format"), target));
    }

    /// <summary>Reports loss of the proxy runtime even under the important-only policy.</summary>
    public void NotifyRuntimeFailure() => Show(NotificationLevel.CriticalOnly,
        GetString("Notification.Runtime.Title"), GetString("Notification.Runtime.UnexpectedExit"));

    public void NotifyProxyFailure() => ReportAvailability("Notification.Runtime.Title", "Notification.Proxy.Unavailable", true);

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

    public Task DeliverTriggerNotificationAsync(
        string idempotencyKey,
        string message,
        CancellationToken cancellationToken)
    {
        string title = GetString("Notification.Custom.Title");
        string detail = string.IsNullOrWhiteSpace(message)
            ? GetString("Notification.Custom.Message")
            : message.Trim();
        return DeliverIdempotentAsync(
            idempotencyKey,
            title,
            detail,
            isFeatureEnabled: true,
            shouldLogFailure: true,
            cancellationToken);
    }

    public Task DeliverTriggerFiredNotificationAsync(
        string idempotencyKey,
        string triggerName,
        bool isTriggerNotificationEnabled,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        return DeliverIdempotentAsync(
            idempotencyKey,
            GetString("Notification.TriggerFired.Title"),
            string.Format(
                CultureInfo.CurrentCulture,
                GetString("Notification.TriggerFired.Message.Format"),
                triggerName),
            isTriggerNotificationEnabled,
            shouldLogFailure: false,
            cancellationToken);
    }

    public void ReportTriggerFiredNotificationFailure(
        string triggerName,
        Exception exception)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(triggerName);
        ArgumentNullException.ThrowIfNull(exception);
        try
        {
            string title = GetString("Notification.TriggerFired.Title");
            string detail = string.Format(
                CultureInfo.CurrentCulture,
                GetString("Notification.TriggerFired.Message.Format"),
                triggerName);
            AppendNotificationLog(
                "Warning",
                GetString("Notification.Log.Failed"),
                title,
                detail,
                exception.Message);
        }
        catch (Exception loggingException) when (IsRecoverableBestEffortFailure(loggingException))
        {
            // Diagnostic storage is best effort and must not revive a contained notification failure.
        }
    }

    public void Show(NotificationLevel minimumLevel, string title, string message)
    {
        if (!ShouldShow(minimumLevel))
        {
            TryAppendNotificationLog("Info", GetString("Notification.Log.Suppressed"), title, message);
            return;
        }

        try
        {
            _platform.Show(title, message);
        }
        catch (Exception exception) when (IsRecoverableBestEffortFailure(exception))
        {
            TryAppendNotificationLog(
                "Warning",
                GetString("Notification.Log.Failed"),
                title,
                message,
                exception.Message);
            return;
        }

        TryAppendNotificationLog("Info", GetString("Notification.Log.Shown"), title, message);
        try
        {
            _triggerEvents.Publish(new TriggerRuntimeEvent(TriggerEventKind.NotificationRaised, minimumLevel));
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            TryAppendNotificationLog(
                "Warning",
                GetString("Notification.Log.Failed"),
                title,
                message,
                exception.Message);
        }
    }

    private bool ShouldShow(NotificationLevel minimumLevel)
    {
        NotificationPolicySnapshot? policy;
        try
        {
            policy = new(_getEnabled(), _getLevel());
            Volatile.Write(ref _lastPolicy, policy);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            // Durable replacement/recovery can temporarily close settings reads. Use only an
            // already observed effective policy; never infer permission from unavailable settings.
            policy = Volatile.Read(ref _lastPolicy);
        }
        if (policy is not { Enabled: true })
        {
            return false;
        }

        NotificationLevel configured = policy.Level;
        return configured switch
        {
            NotificationLevel.CriticalOnly => minimumLevel == NotificationLevel.CriticalOnly,
            NotificationLevel.More => true,
            _ => minimumLevel is NotificationLevel.Default or NotificationLevel.CriticalOnly,
        };
    }

    private async Task DeliverIdempotentAsync(
        string idempotencyKey,
        string title,
        string detail,
        bool isFeatureEnabled,
        bool shouldLogFailure,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (!isFeatureEnabled || !ShouldShow(NotificationLevel.Default))
        {
            AppendNotificationLog("Info", GetString("Notification.Log.Suppressed"), title, detail);
            _triggerReceipts.Record(idempotencyKey);
            return;
        }

        if (await IsTriggerNotificationDeliveredAsync(idempotencyKey, cancellationToken).ConfigureAwait(false))
        {
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
            if (shouldLogFailure)
            {
                AppendNotificationLog(
                    "Warning",
                    GetString("Notification.Log.Failed"),
                    title,
                    detail,
                    exception.Message);
            }

            throw;
        }
    }

    private void AppendNotificationLog(string level, string messageTemplate, string title, string detail, string? error = null)
    {
        string message = error is null
            ? string.Format(CultureInfo.CurrentCulture, messageTemplate, title, detail)
            : string.Format(CultureInfo.CurrentCulture, messageTemplate, title, detail, error);
        _appendLog(level, "Notification", message, BuildNotificationDetail(title, detail, error));
    }

    private void TryAppendNotificationLog(
        string level,
        string messageTemplate,
        string title,
        string detail,
        string? error = null)
    {
        try
        {
            AppendNotificationLog(level, messageTemplate, title, detail, error);
        }
        catch (Exception exception) when (IsRecoverableBestEffortFailure(exception))
        {
            // Notifications and their diagnostics are best effort and cannot break the caller.
        }
    }

    private static bool IsRecoverableBestEffortFailure(Exception exception)
    {
        return StartupCompletionFailurePolicy.IsRecoverable(exception);
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
