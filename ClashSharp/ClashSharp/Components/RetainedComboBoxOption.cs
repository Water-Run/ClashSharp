using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;

namespace ClashSharp.Components;

/// <summary>Keeps display text reachable while WinUI moves a selected item's content into its faceplate.</summary>
internal sealed class RetainedComboBoxOption
{
    private readonly TextBlock _label;

    public RetainedComboBoxOption(string text, object? value,
        TextWrapping wrapping = TextWrapping.NoWrap, double maxWidth = double.PositiveInfinity)
    {
        _label = new TextBlock
        {
            Text = text,
            TextWrapping = wrapping,
            TextTrimming = wrapping == TextWrapping.NoWrap ? TextTrimming.CharacterEllipsis : TextTrimming.None,
            MaxWidth = maxWidth,
        };
        Item = new ComboBoxItem { Content = _label, Tag = value };
        Item.SetBinding(AutomationProperties.NameProperty, new Binding
        {
            Source = _label,
            Path = new PropertyPath(nameof(TextBlock.Text)),
            Mode = BindingMode.OneWay,
        });
    }

    public ComboBoxItem Item { get; }
    public string Text { get => _label.Text; set => _label.Text = value; }
}
