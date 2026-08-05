namespace ClashSharp.ViewModel;

/// <summary>Validated page input used to edit one subscription link.</summary>
internal readonly record struct SubscriptionLinkEditRequest(
    string LinkId,
    string Name,
    string Uri,
    bool IsEnabled,
    int UpdateIntervalHours);
