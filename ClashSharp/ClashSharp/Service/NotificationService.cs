using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
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

    /// <summary>Reports a committed TUN preference and whether it is currently effective.</summary>
    void NotifyTransparentProxyChanged(bool enabled, bool effective) { }

    /// <summary>Reports a verified change to the proxy listener configuration.</summary>
    void NotifyNetworkSettingsChanged() { }

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

/// <summary>Selects one supported transport for the complete notification registration lifetime.</summary>
internal sealed class CompatibleNotificationPlatform : IWin11NotificationPlatform, IWin11NotificationRegistration, IDisposable
{
    private readonly Lazy<IWin11NotificationPlatform> _selected;

    public CompatibleNotificationPlatform(Func<bool> supportsModern,
        IWin11NotificationPlatform modern, IWin11NotificationPlatform fallback)
    {
        ArgumentNullException.ThrowIfNull(supportsModern);
        ArgumentNullException.ThrowIfNull(modern);
        ArgumentNullException.ThrowIfNull(fallback);
        _selected = new(() => supportsModern() ? modern : fallback);
    }

    public IDisposable Register(Action activated)
    {
        ArgumentNullException.ThrowIfNull(activated);
        return _selected.Value is IWin11NotificationRegistration registration
            ? registration.Register(activated) : new NotificationRegistration(static () => { });
    }

    public Task<bool> ContainsAsync(string idempotencyKey, CancellationToken cancellationToken) =>
        _selected.Value.ContainsAsync(idempotencyKey, cancellationToken);

    public void Show(string title, string message, string? idempotencyKey = null) =>
        _selected.Value.Show(title, message, idempotencyKey);

    public void Dispose()
    {
        if (_selected.IsValueCreated && _selected.Value is IDisposable owned) { owned.Dispose(); }
    }
}

/// <summary>Builds text-only toast XML without treating user-visible content as markup.</summary>
internal static class NotificationPayload
{
    public static string Create(string title, string message) =>
        new XElement("toast", new XElement("visual", new XElement("binding",
            new XAttribute("template", "ToastGeneric"),
            new XElement("text", title), new XElement("text", message)))).ToString(SaveOptions.DisableFormatting);
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
    public static IWin11NotificationPlatform Instance { get; } = new CompatibleNotificationPlatform(
        AppNotificationManager.IsSupported, new Win11NotificationPlatform(), new WindowsToastNotificationPlatform());

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

    internal static (string Tag, string Group) CreateIdentity(string idempotencyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey)))
            .ToLowerInvariant();
        return (hash[..16], hash[16..32]);
    }
}

/// <summary>Uses packaged WinRT toasts when Windows App SDK notifications do not support the process token.</summary>
/// <remarks>The built-in Administrator account can remain elevated even after normal registered activation.
/// This transport preserves Windows notification settings and the same manifest activation identity.</remarks>
internal sealed class WindowsToastNotificationPlatform : IWin11NotificationPlatform, IWin11NotificationRegistration, IDisposable
{
    private readonly object _gate = new();
    private readonly List<Windows.UI.Notifications.ToastNotification> _live = [];
    private Action? _activated;

    public IDisposable Register(Action activated)
    {
        ArgumentNullException.ThrowIfNull(activated);
        Volatile.Write(ref _activated, activated);
        return new NotificationRegistration(Dispose);
    }

    public Task<bool> ContainsAsync(string idempotencyKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        (string tag, string group) = Win11NotificationPlatform.CreateIdentity(idempotencyKey);
        bool present = Windows.UI.Notifications.ToastNotificationManager.History.GetHistory()
            .Any(notification => StringComparer.Ordinal.Equals(notification.Tag, tag)
                && StringComparer.Ordinal.Equals(notification.Group, group));
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(present);
    }

    public void Show(string title, string message, string? idempotencyKey = null)
    {
        Windows.Data.Xml.Dom.XmlDocument document = new();
        document.LoadXml(NotificationPayload.Create(title, message));
        Windows.UI.Notifications.ToastNotification notification = new(document);
        if (idempotencyKey is not null)
        {
            (notification.Tag, notification.Group) = Win11NotificationPlatform.CreateIdentity(idempotencyKey);
        }
        lock (_gate)
        {
            // Own a bounded set of callbacks while the main window is alive. Older retained toasts
            // continue to use the packaged COM activation declared in the manifest.
            while (_live.Count >= 128) { Release(_live[0]); }
            _live.Add(notification);
            notification.Activated += OnActivated;
            notification.Dismissed += OnDismissed;
            notification.Failed += OnFailed;
            try { Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier().Show(notification); }
            catch { Release(notification); throw; }
        }
    }

    private void OnActivated(Windows.UI.Notifications.ToastNotification sender, object args)
    {
        Volatile.Read(ref _activated)?.Invoke();
        lock (_gate) { Release(sender); }
    }

    private void OnDismissed(Windows.UI.Notifications.ToastNotification sender,
        Windows.UI.Notifications.ToastDismissedEventArgs args)
    {
        lock (_gate) { Release(sender); }
    }

    private void OnFailed(Windows.UI.Notifications.ToastNotification sender,
        Windows.UI.Notifications.ToastFailedEventArgs args)
    {
        lock (_gate) { Release(sender); }
    }

    private void Release(Windows.UI.Notifications.ToastNotification notification)
    {
        notification.Activated -= OnActivated;
        notification.Dismissed -= OnDismissed;
        notification.Failed -= OnFailed;
        _live.Remove(notification);
    }

    public void Dispose()
    {
        Volatile.Write(ref _activated, null);
        lock (_gate)
        {
            foreach (Windows.UI.Notifications.ToastNotification notification in _live.ToArray()) { Release(notification); }
        }
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

    public void NotifyTransparentProxyChanged(bool enabled, bool effective) => Show(NotificationLevel.Default,
        GetString("Notification.TransparentProxy.Title"), GetString(!enabled ? "Notification.TransparentProxy.Disabled"
            : effective ? "Notification.TransparentProxy.Enabled" : "Notification.TransparentProxy.Pending"));

    public void NotifyNetworkSettingsChanged() => Show(NotificationLevel.Default,
        GetString("Notification.ProxyMode.Title"), GetString("Notification.NetworkSettings.Applied"));

    /// <summary>Uses detailed verbosity for success and the standard policy for actionable failures.</summary>
    public void ReportOperation(string titleResourceKey, string messageResourceKey, bool failed, params object[] arguments)
    {
        try
        {
            ArgumentNullException.ThrowIfNull(arguments);
            Show(GetOperationLevel(messageResourceKey, failed),
                GetString(titleResourceKey), arguments.Length == 0 ? GetString(messageResourceKey)
                    : string.Format(CultureInfo.CurrentCulture, GetString(messageResourceKey), arguments));
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            TryAppendNotificationLog("Warning", "Operation notification failed", titleResourceKey, messageResourceKey, exception.GetType().Name);
        }
    }

    // Availability loss/recovery uses ReportAvailability. These are verified changes
    // to the user's active routing; routine successful management remains detailed.
    internal static NotificationLevel GetOperationLevel(string messageResourceKey, bool failed) =>
        messageResourceKey switch
        {
            "Notification.Startup.Attention" => NotificationLevel.CriticalOnly,
            _ when failed => NotificationLevel.Default,
            "Profiles.Status.Activated" or "ProxyNodes.Status.SelectionApplied" => NotificationLevel.Default,
            _ => NotificationLevel.More,
        };

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
