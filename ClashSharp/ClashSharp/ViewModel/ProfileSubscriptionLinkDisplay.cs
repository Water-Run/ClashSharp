using System;
using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a subscription link.</summary>
internal sealed record ProfileSubscriptionLinkDisplay(
    ProfileSubscriptionLink Model,
    string NameDisplay,
    string UriDisplay,
    string StatusDisplay)
{
    public bool IsEnabled => Model.IsEnabled;

    public int UpdateIntervalHours => Model.UpdateIntervalHours;

    public DateTimeOffset LastUpdatedAt => Model.LastUpdatedAt;

    public string LastUpdatedDisplay => Model.LastUpdatedAt <= DateTimeOffset.UnixEpoch
        ? "—"
        : Model.LastUpdatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}
