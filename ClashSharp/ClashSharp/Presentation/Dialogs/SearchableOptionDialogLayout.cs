using System;
using ClashSharp.Components;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Keeps a searchable option dialog stable while filtering and responsive to its window.</summary>
internal sealed class SearchableOptionDialogLayout : IDisposable
{
    private readonly ContentDialog _dialog;
    private readonly SearchableOptionList _content;
    private readonly XamlRoot _root;

    public SearchableOptionDialogLayout(ContentDialog dialog, SearchableOptionList content)
    {
        _dialog = dialog;
        _content = content;
        _root = dialog.XamlRoot ?? throw new ArgumentException("The dialog must have a XamlRoot.", nameof(dialog));
        UpdateBounds();
        _root.Changed += Root_Changed;
    }

    public void Dispose() => _root.Changed -= Root_Changed;

    private void Root_Changed(XamlRoot sender, XamlRootChangedEventArgs args) => UpdateBounds();

    private void UpdateBounds()
    {
        double width = Math.Clamp(_root.Size.Width - 96, 280, 560);
        if (_content.Width != width)
        {
            _content.Width = width;
            _dialog.Resources["ContentDialogMaxWidth"] = width + 48d;
        }
        _content.MaxListHeight = Math.Clamp(_root.Size.Height - 240, 80, 360);
    }
}
