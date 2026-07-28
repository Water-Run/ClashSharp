using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a routing rule preview.</summary>
internal sealed record RulePreviewDisplay(
    RulePreview Model,
    string ProviderNameDisplay,
    string PayloadDisplay)
{
    public string RuleType => Model.RuleType;

    public string Action => Model.Action;

    public long HitCount => Model.HitCount;
}
