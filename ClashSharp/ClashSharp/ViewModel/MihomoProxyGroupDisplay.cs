using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable presentation row for a mihomo strategy group.</summary>
internal sealed record MihomoProxyGroupDisplay(
    MihomoProxyGroup Model,
    string NameDisplay,
    string CurrentSelectionDisplay)
{
    public string Type => Model.Type;

    public string CurrentSelection => Model.CurrentSelection;

    public IReadOnlyList<string> Candidates => Model.Candidates;
}
