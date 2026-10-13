using System;
using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a subscription link.</summary>
internal sealed record ProfileSubscriptionLinkDisplay(
    ProfileSubscriptionLink Model,
    string NameDisplay,
    string UriDisplay,
    string StatusDisplay)
{
    internal DisplayFormatter Formatter { get; init; } = new();
    public bool IsEnabled => Model.IsEnabled;

    public int UpdateIntervalHours => Model.UpdateIntervalHours;

    public DateTimeOffset LastUpdatedAt => Model.LastUpdatedAt;

    public string LastUpdatedDisplay => Model.LastUpdatedAt <= DateTimeOffset.UnixEpoch
        ? "—"
        : Formatter.Timestamp(Model.LastUpdatedAt, includeSeconds: false);

    public string ScheduleDisplay { get; init; } = string.Empty;

    public string UpdatedSummary { get; init; } = string.Empty;

    public string UsageDisplay { get; init; } = string.Empty;

    public string ExpiryDisplay { get; init; } = string.Empty;

    /// <summary>Adds localized labels without changing provider data or filtered display text.</summary>
    public ProfileSubscriptionLinkDisplay WithDetails(Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        SubscriptionUsage? usage = Model.Usage;
        string missing = getString("Links.Metadata.NotProvided");
        string used = usage is { UploadBytes: >= 0, DownloadBytes: >= 0 }
            ? FormatBytes((decimal)usage.UploadBytes.Value + usage.DownloadBytes.Value) : missing;
        string total = usage?.TotalBytes is >= 0 ? FormatBytes(usage.TotalBytes.Value) : missing;
        string expiry = usage?.ExpireUnixSeconds switch
        {
            0 => getString("Links.Metadata.NoExpiry"),
            > 0 and <= 253402300799 => Formatter.Timestamp(DateTimeOffset.FromUnixTimeSeconds(usage.ExpireUnixSeconds.Value), includeSeconds: false),
            _ => missing,
        };
        return this with
        {
            ScheduleDisplay = Model.IsEnabled
                ? string.Format(CultureInfo.CurrentCulture, getString("Links.Schedule.Automatic.Format"), Model.UpdateIntervalHours)
                : getString("Links.Schedule.Manual"),
            UpdatedSummary = string.Format(CultureInfo.CurrentCulture, getString("Links.Updated.Format"), LastUpdatedDisplay),
            UsageDisplay = string.Format(CultureInfo.CurrentCulture, getString("Links.Usage.Format"), used, total),
            ExpiryDisplay = string.Format(CultureInfo.CurrentCulture, getString("Links.Expiry.Format"), expiry),
        };
    }

    private string FormatBytes(decimal bytes) => Formatter.Bytes(bytes);
}
