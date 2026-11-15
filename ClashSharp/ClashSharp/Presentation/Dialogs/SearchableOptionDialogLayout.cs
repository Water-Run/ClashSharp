using System;
using ClashSharp.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Keeps a searchable option dialog stable while filtering and responsive to its window.</summary>
internal sealed class SearchableOptionDialogLayout : IDisposable
{
    // Leave room for the title bar, dialog heading, search input and fixed action row.
    private const double VerticalChrome = 312;
    private readonly ContentDialog _dialog;
    private readonly SearchableOptionList _content;
    private readonly XamlRoot _root;

    public SearchableOptionDialogLayout(ThemedContentDialog dialog, SearchableOptionList content)
    {
        _dialog = dialog;
        _content = content;
        _root = dialog.OwnerXamlRoot ?? throw new ArgumentException("The dialog must have an owning XamlRoot.", nameof(dialog));
        UpdateBounds();
        _root.Changed += Root_Changed;
    }

    public void Dispose() => _root.Changed -= Root_Changed;

    /// <summary>Remeasures translated header and ordering instructions without replacing list items.</summary>
    internal void RefreshLayout() => UpdateBounds();

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();

    private void UpdateBounds()
    {
        var available = DialogViewport.GetAvailableSize(_root);
        double width = Math.Max(1, Math.Min(available.Width - 96, 560));
        if (_content.Width != width)
        {
            _content.Width = width;
            _dialog.Resources["ContentDialogMaxWidth"] = width + 48d;
        }
        double headerHeight = string.IsNullOrWhiteSpace(_content.SearchHeader) ? 0 : 32;
        double reorderHintHeight = _content.MeasureReorderHintHeight(width);
        _content.MaxListHeight = Math.Max(1, Math.Min(available.Height - VerticalChrome - headerHeight - reorderHintHeight, 360));
    }
}
