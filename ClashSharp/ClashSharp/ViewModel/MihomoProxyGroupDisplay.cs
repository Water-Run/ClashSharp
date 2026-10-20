using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a mihomo strategy group.</summary>
internal sealed class MihomoProxyGroupDisplay(
    MihomoProxyGroup model,
    string nameDisplay,
    string currentSelectionDisplay) : ObservableObject
{
    private string _typeDisplay = model.Type;
    public MihomoProxyGroup Model { get; } = model;
    public string NameDisplay { get; } = nameDisplay;
    public string CurrentSelectionDisplay { get; } = currentSelectionDisplay;
    public string Type => Model.Type;

    public string TypeDisplay { get => _typeDisplay; internal set => SetProperty(ref _typeDisplay, value); }

    public string CurrentSelection => Model.CurrentSelection;

    public IReadOnlyList<string> Candidates => Model.Candidates;
}
