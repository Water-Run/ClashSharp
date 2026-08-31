using System.Windows.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Reusable mode-selection button with title, description, icon, selected state, and command.</summary>
public sealed partial class MasterModeButton : UserControl
{
    /// <summary>Identifies the <see cref="Title"/> dependency property.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(MasterModeButton),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Description"/> dependency property.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(MasterModeButton),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="Glyph"/> dependency property.</summary>
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(
        nameof(Glyph),
        typeof(string),
        typeof(MasterModeButton),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="IsChecked"/> dependency property.</summary>
    public static readonly DependencyProperty IsCheckedProperty = DependencyProperty.Register(
        nameof(IsChecked),
        typeof(bool),
        typeof(MasterModeButton),
        new PropertyMetadata(false, OnIsCheckedChanged));

    /// <summary>Identifies the <see cref="Command"/> dependency property.</summary>
    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command),
        typeof(ICommand),
        typeof(MasterModeButton),
        new PropertyMetadata(null));

    /// <summary>Initializes a mode button and its selection visual states.</summary>
    public MasterModeButton()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdateVisualState(useTransitions: false);
    }

    /// <summary>Gets or sets the mode's primary display label.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets the concise explanation of the mode's behavior.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Gets or sets the glyph used to identify the mode.</summary>
    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>Gets or sets whether this button represents the selected mode.</summary>
    public bool IsChecked
    {
        get => (bool)GetValue(IsCheckedProperty);
        set => SetValue(IsCheckedProperty, value);
    }

    /// <summary>Gets or sets the command that requests activation of this mode.</summary>
    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    private static void OnIsCheckedChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        if (dependencyObject is MasterModeButton button)
        {
            button.UpdateVisualState(useTransitions: true);
        }
    }

    private void UpdateVisualState(bool useTransitions)
    {
        _ = VisualStateManager.GoToState(this, IsChecked ? "SelectedOn" : "SelectedOff", useTransitions);
    }
}
