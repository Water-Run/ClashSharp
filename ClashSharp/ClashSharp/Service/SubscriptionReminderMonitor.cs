using System;
using System.Collections.Generic;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using Windows.Storage;

namespace ClashSharp.Service;

/// <summary>Which provider-reported limit a subscription reminder concerns.</summary>
internal enum SubscriptionReminderDimension
{
    /// <summary>The subscription's expiry time.</summary>
    Expiry,

    /// <summary>The subscription's data quota.</summary>
    Quota,
}

/// <summary>How close a subscription is to one limit; a larger value is more urgent.</summary>
internal enum SubscriptionReminderStage
{
    /// <summary>The limit is not near, or nothing is known about it.</summary>
    None = 0,

    /// <summary>The limit is near: expiry within the notice period, or most of the quota used.</summary>
    Approaching = 1,

    /// <summary>The limit has been reached: the subscription expired, or the quota is used up.</summary>
    Reached = 2,
}

/// <summary>One evaluated limit and the period it belongs to.</summary>
/// <param name="Stage">How close the limit is.</param>
/// <param name="Cycle">
/// Identifies the limit period, the expiry instant or the quota size, so that a renewal or a new plan starts a
/// new period. Null when the provider reported nothing usable, which carries no information.
/// </param>
internal readonly record struct SubscriptionReminderState(SubscriptionReminderStage Stage, string? Cycle);

/// <summary>
/// Decides when a subscription limit deserves a notification. A reminder is sent once when a limit comes near and
/// once more when it is reached, never again for the same period, and it re-arms when the period changes.
/// </summary>
internal static class SubscriptionReminderPolicy
{
    /// <summary>How long before expiry the first reminder is sent.</summary>
    internal static readonly TimeSpan ExpiryNotice = TimeSpan.FromDays(3);

    /// <summary>The used share of the quota at which the first reminder is sent.</summary>
    internal const decimal QuotaNoticeFraction = 0.9m;

    private const long LatestUnixSeconds = 253402300799;

    /// <summary>Evaluates the expiry limit at the supplied time.</summary>
    /// <param name="usage">Provider metadata, or null when none was reported.</param>
    /// <param name="now">Current time.</param>
    internal static SubscriptionReminderState EvaluateExpiry(SubscriptionUsage? usage, DateTimeOffset now)
    {
        long? seconds = usage?.ExpireUnixSeconds;
        if (seconds is null or < 0 or > LatestUnixSeconds) { return new(SubscriptionReminderStage.None, null); }
        string cycle = seconds.Value.ToString(CultureInfo.InvariantCulture);
        // Zero is the provider's way of saying the subscription never expires.
        if (seconds == 0) { return new(SubscriptionReminderStage.None, cycle); }
        TimeSpan remaining = DateTimeOffset.FromUnixTimeSeconds(seconds.Value) - now;
        SubscriptionReminderStage stage = remaining <= TimeSpan.Zero ? SubscriptionReminderStage.Reached
            : remaining <= ExpiryNotice ? SubscriptionReminderStage.Approaching : SubscriptionReminderStage.None;
        return new(stage, cycle);
    }

    /// <summary>Evaluates the data quota limit.</summary>
    /// <param name="usage">Provider metadata, or null when none was reported.</param>
    internal static SubscriptionReminderState EvaluateQuota(SubscriptionUsage? usage)
    {
        if (usage is not { UploadBytes: >= 0, DownloadBytes: >= 0, TotalBytes: > 0 })
        {
            return new(SubscriptionReminderStage.None, null);
        }

        decimal used = (decimal)usage.UploadBytes.Value + usage.DownloadBytes.Value;
        decimal total = usage.TotalBytes.Value;
        SubscriptionReminderStage stage = used >= total ? SubscriptionReminderStage.Reached
            : used >= total * QuotaNoticeFraction ? SubscriptionReminderStage.Approaching : SubscriptionReminderStage.None;
        return new(stage, usage.TotalBytes.Value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Decides whether the current state is announced and what is remembered afterwards.</summary>
    /// <param name="dimension">The limit being decided.</param>
    /// <param name="remembered">The last announced state for this subscription and limit, or null.</param>
    /// <param name="current">The state just evaluated.</param>
    /// <returns>Whether to notify, and the state to remember (null forgets it).</returns>
    internal static (bool Notify, SubscriptionReminderState? Remember) Decide(
        SubscriptionReminderDimension dimension,
        SubscriptionReminderState? remembered,
        SubscriptionReminderState current)
    {
        // Missing metadata says nothing about the limit, so it neither announces nor forgets anything.
        if (current.Cycle is null) { return (false, remembered); }
        bool samePeriod = remembered is { } known && StringComparer.Ordinal.Equals(known.Cycle, current.Cycle);
        if (current.Stage == SubscriptionReminderStage.None)
        {
            // A renewal moves the expiry and a provider reset brings used data back under the threshold:
            // either starts a new period whose reminders must fire again.
            bool rearm = remembered is not null && (dimension == SubscriptionReminderDimension.Quota || !samePeriod);
            return (false, rearm ? null : remembered);
        }

        return samePeriod && remembered!.Value.Stage >= current.Stage ? (false, remembered) : (true, current);
    }

    /// <summary>Serializes a remembered state for storage.</summary>
    internal static string Format(SubscriptionReminderState state) =>
        ((int)state.Stage).ToString(CultureInfo.InvariantCulture) + "|" + state.Cycle;

    /// <summary>Reads a stored state; anything malformed is treated as nothing remembered.</summary>
    internal static SubscriptionReminderState? Parse(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) { return null; }
        int separator = stored.IndexOf('|', StringComparison.Ordinal);
        if (separator <= 0 || separator == stored.Length - 1) { return null; }
        return int.TryParse(stored.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out int stage)
            && stage is (int)SubscriptionReminderStage.Approaching or (int)SubscriptionReminderStage.Reached
                ? new SubscriptionReminderState((SubscriptionReminderStage)stage, stored[(separator + 1)..]) : null;
    }
}

/// <summary>Durable memory of announced subscription reminders.</summary>
internal interface ISubscriptionReminderStore
{
    /// <summary>Reads a stored value, or null.</summary>
    string? Read(string key);

    /// <summary>Stores a value; null removes it.</summary>
    void Write(string key, string? value);
}

/// <summary>Keeps reminder memory in the package's local settings, falling back to memory without package identity.</summary>
internal sealed class LocalSubscriptionReminderStore : ISubscriptionReminderStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _fallback = new(StringComparer.Ordinal);
    private readonly ApplicationDataContainer? _settings;

    public LocalSubscriptionReminderStore()
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

    public string? Read(string key)
    {
        lock (_gate)
        {
            if (_settings is null) { return _fallback.TryGetValue(key, out string? value) ? value : null; }
            return _settings.Values.TryGetValue(key, out object? stored) ? stored as string : null;
        }
    }

    public void Write(string key, string? value)
    {
        lock (_gate)
        {
            if (_settings is null)
            {
                if (value is null) { _fallback.Remove(key); } else { _fallback[key] = value; }
                return;
            }

            if (value is null) { _settings.Values.Remove(key); } else { _settings.Values[key] = value; }
        }
    }
}

/// <summary>
/// Reminds the user, through Windows notifications, that the active subscription is about to expire or run out
/// of data, and again when it has. The check runs after every subscription update pass, so it sees the newest
/// provider metadata and also notices an expiry that passes while nothing changes.
/// </summary>
/// <remarks>
/// Only the active subscription is watched: it is the one the network depends on. Reminders follow the
/// user's notification switch and level; an approaching limit is standard content and a reached limit is
/// important, because the nodes may already have stopped working.
/// </remarks>
internal sealed class SubscriptionReminderMonitor
{
    private const string KeyPrefix = "SubscriptionReminderV1.";

    private readonly Func<ProfileSubscriptionLink?> _readActiveSubscription;
    private readonly ISubscriptionReminderStore _store;
    private readonly Action<NotificationLevel, string, string> _show;
    private readonly Func<string, string> _getString;
    private readonly DisplayFormatter _formatter;
    private readonly TimeProvider _time;
    private readonly object _gate = new();

    /// <summary>Creates the monitor.</summary>
    /// <param name="readActiveSubscription">Reads the active subscription link, or null for a local profile.</param>
    /// <param name="store">Durable memory of announced reminders.</param>
    /// <param name="show">Shows a notification under the user's switch and level.</param>
    /// <param name="getString">Thread-safe localized text lookup.</param>
    /// <param name="formatter">Display formatter for sizes and times.</param>
    /// <param name="time">Clock.</param>
    internal SubscriptionReminderMonitor(
        Func<ProfileSubscriptionLink?> readActiveSubscription,
        ISubscriptionReminderStore store,
        Action<NotificationLevel, string, string> show,
        Func<string, string> getString,
        DisplayFormatter formatter,
        TimeProvider time)
    {
        _readActiveSubscription = readActiveSubscription ?? throw new ArgumentNullException(nameof(readActiveSubscription));
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _show = show ?? throw new ArgumentNullException(nameof(show));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));
        _time = time ?? throw new ArgumentNullException(nameof(time));
    }

    /// <summary>Evaluates the active subscription and sends any reminder that is due.</summary>
    internal void Evaluate()
    {
        if (_readActiveSubscription() is not { } link) { return; }
        DateTimeOffset now = _time.GetUtcNow();
        lock (_gate)
        {
            Apply(link, SubscriptionReminderDimension.Expiry, SubscriptionReminderPolicy.EvaluateExpiry(link.Usage, now), now);
            Apply(link, SubscriptionReminderDimension.Quota, SubscriptionReminderPolicy.EvaluateQuota(link.Usage), now);
        }
    }

    /// <summary>Builds the storage key for one subscription and limit; the link identity is hashed.</summary>
    internal static string CreateKey(string linkId, SubscriptionReminderDimension dimension)
    {
        ArgumentException.ThrowIfNullOrEmpty(linkId);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(linkId)))[..24].ToLowerInvariant();
        return KeyPrefix + hash + "." + (dimension == SubscriptionReminderDimension.Expiry ? "expiry" : "quota");
    }

    private void Apply(ProfileSubscriptionLink link, SubscriptionReminderDimension dimension,
        SubscriptionReminderState current, DateTimeOffset now)
    {
        string key = CreateKey(link.Id, dimension);
        SubscriptionReminderState? remembered = SubscriptionReminderPolicy.Parse(_store.Read(key));
        (bool notify, SubscriptionReminderState? remember) = SubscriptionReminderPolicy.Decide(dimension, remembered, current);
        if (notify)
        {
            (string title, string message) = Compose(link, dimension, current.Stage, now);
            _show(current.Stage == SubscriptionReminderStage.Reached ? NotificationLevel.CriticalOnly : NotificationLevel.Default,
                title, message);
        }

        if (remember != remembered)
        {
            _store.Write(key, remember is { } state ? SubscriptionReminderPolicy.Format(state) : null);
        }
    }

    private (string Title, string Message) Compose(ProfileSubscriptionLink link, SubscriptionReminderDimension dimension,
        SubscriptionReminderStage stage, DateTimeOffset now)
    {
        SubscriptionUsage usage = link.Usage!;
        string name = link.Name;
        if (dimension == SubscriptionReminderDimension.Expiry)
        {
            DateTimeOffset expiry = DateTimeOffset.FromUnixTimeSeconds(usage.ExpireUnixSeconds!.Value);
            string when = _formatter.Timestamp(expiry, includeSeconds: false);
            return stage == SubscriptionReminderStage.Reached
                ? (_getString("Notification.Subscription.Expired.Title"),
                    Format("Notification.Subscription.Expired.Message.Format", name, when))
                : (_getString("Notification.Subscription.ExpiresSoon.Title"),
                    Format("Notification.Subscription.ExpiresSoon.Message.Format", name, when,
                        Math.Max(1, (int)Math.Ceiling((expiry - now).TotalDays))));
        }

        decimal used = (decimal)usage.UploadBytes!.Value + usage.DownloadBytes!.Value;
        decimal total = usage.TotalBytes!.Value;
        return stage == SubscriptionReminderStage.Reached
            ? (_getString("Notification.Subscription.QuotaExhausted.Title"),
                Format("Notification.Subscription.QuotaExhausted.Message.Format", name, _formatter.Bytes(used), _formatter.Bytes(total)))
            : (_getString("Notification.Subscription.QuotaLow.Title"),
                Format("Notification.Subscription.QuotaLow.Message.Format", name, _formatter.Bytes(used), _formatter.Bytes(total),
                    _formatter.Bytes(total - used)));
    }

    private string Format(string key, params object[] arguments) =>
        string.Format(CultureInfo.CurrentCulture, _getString(key), arguments);
}
