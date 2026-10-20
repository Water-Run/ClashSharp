using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a routing rule preview.</summary>
internal sealed class RulePreviewDisplay(
    RulePreview model,
    string providerNameDisplay,
    string payloadDisplay) : ObservableObject
{
    private string _providerNameDisplay = providerNameDisplay;
    public RulePreview Model { get; } = model;
    public string ProviderNameDisplay => _providerNameDisplay;
    public string PayloadDisplay { get; } = payloadDisplay;

    /// <summary>Relocalizes only explicitly tagged application labels while retaining the rule and its row.</summary>
    public void RefreshLocalization(Func<string, string> getString, Func<string, string> filter)
    {
        if (Model.ProviderResourceKey is string key)
        { SetProperty(ref _providerNameDisplay, filter(getString(key)), nameof(ProviderNameDisplay)); }
    }
    public string RuleType => Model.RuleType;

    public string Action => Model.Action;

    public long HitCount => Model.HitCount;
}
