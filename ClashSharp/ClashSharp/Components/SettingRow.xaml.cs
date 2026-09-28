using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>A settings row that moves its actions below the text when horizontal space is limited.</summary>
/// <remarks>
/// Invariants: Title and description default to empty strings.
/// Thread safety: Must be created and accessed from the UI thread only.
/// Side effects: None beyond normal dependency property updates.
/// </remarks>
public sealed partial class SettingRow : UserControl
{
    private const double MinimumTextColumnWidth = 280;
    private bool _actionsBelow;

    /// <summary>Dependency property backing <see cref="Title"/>.</summary>
    public static readonly DependencyProperty TitleProperty = DependencyProperty.Register(
        nameof(Title),
        typeof(string),
        typeof(SettingRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Dependency property backing <see cref="Description"/>.</summary>
    public static readonly DependencyProperty DescriptionProperty = DependencyProperty.Register(
        nameof(Description),
        typeof(string),
        typeof(SettingRow),
        new PropertyMetadata(string.Empty));

    /// <summary>Dependency property backing <see cref="ActionContent"/>.</summary>
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent),
        typeof(object),
        typeof(SettingRow),
        new PropertyMetadata(null));

    /// <summary>Initializes the settings row component.</summary>
    public SettingRow()
    {
        InitializeComponent();
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LayoutRoot is null || ActionContentPresenter is null || LayoutRoot.ActualWidth <= 0)
        {
            return;
        }

        // Use the row's available space and the actual localized controls, not a window breakpoint.
        double contentWidth = LayoutRoot.ActualWidth
            - LayoutRoot.Padding.Left - LayoutRoot.Padding.Right
            - LayoutRoot.BorderThickness.Left - LayoutRoot.BorderThickness.Right;
        double actionWidth = ActionContentPresenter.DesiredSize.Width;
        bool actionsBelow = actionWidth > 0
            && contentWidth - actionWidth - LayoutRoot.ColumnSpacing < MinimumTextColumnWidth;
        if (_actionsBelow == actionsBelow)
        {
            return;
        }

        _actionsBelow = actionsBelow;
        Grid.SetColumnSpan(TextPanel, actionsBelow ? 2 : 1);
        Grid.SetRow(ActionContentPresenter, actionsBelow ? 1 : 0);
        Grid.SetColumn(ActionContentPresenter, actionsBelow ? 0 : 1);
        Grid.SetColumnSpan(ActionContentPresenter, actionsBelow ? 2 : 1);
        ActionContentPresenter.HorizontalAlignment = actionsBelow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ActionContentPresenter.Margin = actionsBelow ? new Thickness(0, 12, 0, 0) : new Thickness(0);
    }

    /// <summary>Gets or sets the primary row title.</summary>
    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    /// <summary>Gets or sets the secondary row description.</summary>
    public string Description
    {
        get => (string)GetValue(DescriptionProperty);
        set => SetValue(DescriptionProperty, value);
    }

    /// <summary>Gets or sets the action control displayed beside or below the row text.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }
}
