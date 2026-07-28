using System;
using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a mihomo provider resource.</summary>
internal sealed record MihomoProviderResourceDisplay(
    MihomoProviderResource Model,
    string NameDisplay)
{
    public string TypeDisplay =>
        Model.Kind == MihomoProviderKind.Proxy ? "Proxy Provider" : "Rule Provider";

    public string DetailDisplay => Model.Kind == MihomoProviderKind.Proxy
        ? string.IsNullOrWhiteSpace(Model.VehicleType) ? "Proxy" : Model.VehicleType
        : string.IsNullOrWhiteSpace(Model.Behavior) ? "Rule" : Model.Behavior;

    public string ItemCountDisplay =>
        Model.ItemCount.ToString("N0", CultureInfo.CurrentCulture);

    public string UpdatedAtDisplay =>
        Model.UpdatedAt?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "-";
}
