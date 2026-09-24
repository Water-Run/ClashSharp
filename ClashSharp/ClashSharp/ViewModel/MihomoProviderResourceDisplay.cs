using System;
using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a mihomo provider resource.</summary>
internal sealed class MihomoProviderResourceDisplay(
    MihomoProviderResource model,
    string nameDisplay) : ObservableObject
{
    public MihomoProviderResource Model { get; private set; } = model;

    public string NameDisplay { get; private set; } = nameDisplay;

    public string UpdateActionText { get; set; } = string.Empty;

    /// <summary>Refreshes values without replacing the row or its focused controls.</summary>
    public void UpdateFrom(MihomoProviderResourceDisplay updated)
    {
        ArgumentNullException.ThrowIfNull(updated);
        if (Model.Kind != updated.Model.Kind || !StringComparer.Ordinal.Equals(Model.Name, updated.Model.Name))
        {
            throw new ArgumentException("Provider identity must remain unchanged.", nameof(updated));
        }

        if (Model == updated.Model && NameDisplay == updated.NameDisplay && UpdateActionText == updated.UpdateActionText)
        {
            return;
        }

        Model = updated.Model;
        NameDisplay = updated.NameDisplay;
        UpdateActionText = updated.UpdateActionText;
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(NameDisplay));
        OnPropertyChanged(nameof(UpdateActionText));
        OnPropertyChanged(nameof(TypeDisplay));
        OnPropertyChanged(nameof(DetailDisplay));
        OnPropertyChanged(nameof(ItemCountDisplay));
        OnPropertyChanged(nameof(UpdatedAtDisplay));
    }

    public string TypeDisplay =>
        Model.Kind == MihomoProviderKind.Proxy ? "Proxy Provider" : "Rule Provider";

    public string DetailDisplay => Model.Kind == MihomoProviderKind.Proxy
        ? string.IsNullOrWhiteSpace(Model.VehicleType) ? "Proxy" : Model.VehicleType
        : string.IsNullOrWhiteSpace(Model.Behavior) ? "Rule" : Model.Behavior;

    public string ItemCountDisplay =>
        Model.ItemCount.ToString("N0", CultureInfo.CurrentCulture);

    public string UpdatedAtDisplay =>
        Model.UpdatedAt is DateTimeOffset updatedAt && updatedAt > DateTimeOffset.MinValue
            ? updatedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)
            : "-";
}
