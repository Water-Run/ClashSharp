using System;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.ViewModel;

/// <summary>One failed application with immutable recovery identity and refreshable display labels.</summary>
internal sealed class SettingsApplicationIssue(
    string title, string retryText, SettingsAuthoritySnapshot snapshot, Guid batchId, string? titleResourceKey = null) : ObservableObject
{
    private string _title = title;
    private string _retryText = retryText;
    public string Title => _title;
    public string RetryText => _retryText;
    public SettingsAuthoritySnapshot Snapshot { get; } = snapshot;
    public Guid BatchId { get; } = batchId;

    public void RefreshLocalization(Func<string, string> getString)
    {
        if (titleResourceKey is not null) { SetProperty(ref _title, getString(titleResourceKey), nameof(Title)); }
        SetProperty(ref _retryText, getString("Settings.Reset.Retry"), nameof(RetryText));
    }
}
