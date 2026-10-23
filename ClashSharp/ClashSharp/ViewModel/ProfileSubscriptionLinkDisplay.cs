using System;
using System.Globalization;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a subscription link.</summary>
internal sealed class ProfileSubscriptionLinkDisplay(
    ProfileSubscriptionLink model,
    string nameDisplay,
    string uriDisplay,
    string statusDisplay) : ObservableObject
{
    private string _scheduleDisplay = string.Empty;
    private string _updatedSummary = string.Empty;
    private string _usageDisplay = string.Empty;
    private string _expiryDisplay = string.Empty;
    private string _statusDisplay = statusDisplay;
    public ProfileSubscriptionLink Model { get; } = model;
    public string NameDisplay { get; } = nameDisplay;
    public string UriDisplay { get; } = uriDisplay;
    public string StatusDisplay { get => _statusDisplay; private set => SetProperty(ref _statusDisplay, value); }
    internal Func<Func<string, string>, string>? StatusTextFactory { get; init; }
    internal DisplayFormatter Formatter { get; init; } = new();
    public bool IsEnabled => Model.IsEnabled;

    public int UpdateIntervalHours => Model.UpdateIntervalHours;

    public DateTimeOffset LastUpdatedAt => Model.LastUpdatedAt;

    public string LastUpdatedDisplay => Model.LastUpdatedAt <= DateTimeOffset.UnixEpoch
        ? "—"
        : Formatter.Timestamp(Model.LastUpdatedAt, includeSeconds: false);

    public string ScheduleDisplay { get => _scheduleDisplay; private set => SetProperty(ref _scheduleDisplay, value); }

    public string UpdatedSummary { get => _updatedSummary; private set => SetProperty(ref _updatedSummary, value); }

    public string UsageDisplay { get => _usageDisplay; private set => SetProperty(ref _usageDisplay, value); }

    public string ExpiryDisplay { get => _expiryDisplay; private set => SetProperty(ref _expiryDisplay, value); }

    /// <summary>Adds localized labels without changing provider data or filtered display text.</summary>
    public ProfileSubscriptionLinkDisplay WithDetails(Func<string, string> getString)
    {
        ProfileSubscriptionLinkDisplay row = new(Model, NameDisplay, UriDisplay, StatusDisplay)
        { Formatter = Formatter, StatusTextFactory = StatusTextFactory };
        row.RefreshLocalization(getString);
        return row;
    }

    /// <summary>Updates labels while preserving the selected link, provider reply and immutable model.</summary>
    public void RefreshLocalization(Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);
        if (StatusTextFactory is not null) { StatusDisplay = StatusTextFactory(getString); }
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
        ScheduleDisplay = Model.IsEnabled
                ? string.Format(CultureInfo.CurrentCulture, getString("Links.Schedule.Automatic.Format"), Model.UpdateIntervalHours)
                : getString("Links.Schedule.Manual");
        UpdatedSummary = string.Format(CultureInfo.CurrentCulture, getString("Links.Updated.Format"), LastUpdatedDisplay);
        UsageDisplay = string.Format(CultureInfo.CurrentCulture, getString("Links.Usage.Format"), used, total);
        ExpiryDisplay = string.Format(CultureInfo.CurrentCulture, getString("Links.Expiry.Format"), expiry);
        OnPropertyChanged(nameof(LastUpdatedDisplay));
    }

    private string FormatBytes(decimal bytes) => Formatter.Bytes(bytes);
}
