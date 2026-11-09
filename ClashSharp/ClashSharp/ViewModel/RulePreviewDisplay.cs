using System;
using System.Globalization;
using System.Linq;
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
        OnPropertyChanged(nameof(HitCountDisplay));
        if (Model.ProviderResourceKey is string key)
        { SetProperty(ref _providerNameDisplay, filter(getString(key)), nameof(ProviderNameDisplay)); }
    }
    public string RuleType => Model.RuleType;

    public string Action => Model.Action;

    public long HitCount => Model.HitCount;

    public string HitCountDisplay => Model.IsHitCountKnown ? Model.HitCount.ToString("N0", CultureInfo.CurrentCulture) : "—";

    /// <summary>Gives screen readers a meaningful row name; the default would be the CLR type name.</summary>
    public override string ToString() => string.Join(", ", new[] { RuleType, PayloadDisplay, Action }.Where(part => !string.IsNullOrWhiteSpace(part)));
}
