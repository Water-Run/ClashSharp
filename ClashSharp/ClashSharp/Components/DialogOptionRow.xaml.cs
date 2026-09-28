using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Reusable option row for dialog choice lists.</summary>
public sealed partial class DialogOptionRow : UserControl
{
    /// <summary>Identifies the <see cref="Title"/> dependency property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Metadata"/> dependency property.</summary>
    public static readonly DependencyProperty MetadataProperty = DependencyProperty.Register(
        nameof(Metadata),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Description"/> dependency property.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Glyph"/> dependency property.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(DialogOptionRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Initializes a dialog option row and its generated visual tree.</summary>
    public DialogOptionRow()
    {
        InitializeComponent();
    }

    /// <summary>Gets or sets the option's primary display label.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets compact secondary metadata shown beside the title.</summary>
    public string Metadata
    {
        get => (string)GetValue(MetadataProperty);
        set => SetValue(MetadataProperty, value);
    }

    /// <summary>Gets or sets the explanatory text displayed below the option title.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Gets or sets the glyph rendered for the option.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

}
