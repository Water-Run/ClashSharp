using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a proxy node.</summary>
internal sealed record ProxyNodeDisplay(ProxyNode Model, string NameDisplay)
{
    public string Protocol => Model.Protocol;

    public RegionMetadata Region => Model.Region;

    public int? LatencyMilliseconds => Model.LatencyMilliseconds;
}
