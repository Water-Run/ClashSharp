using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>A Windows 11 settings card that moves its actions below the text when horizontal space is limited.</summary>
/// <remarks>
/// Invariants: Title, description and icon default to empty strings; an empty icon or description is collapsed.
/// Thread safety: Must be created and accessed from the UI thread only.
/// Side effects: None beyond normal dependency property updates.
/// </remarks>
public sealed partial class SettingRow : UserControl
{
    private const double MinimumTextColumnWidth = 240;
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
        new PropertyMetadata(string.Empty, OnPresentationPropertyChanged));

    /// <summary>Dependency property backing <see cref="Icon"/>.</summary>
    public static readonly DependencyProperty IconProperty = DependencyProperty.Register(
        nameof(Icon),
        typeof(string),
        typeof(SettingRow),
        new PropertyMetadata(string.Empty, OnPresentationPropertyChanged));

    /// <summary>Dependency property backing <see cref="ActionContent"/>.</summary>
    public static readonly DependencyProperty ActionContentProperty = DependencyProperty.Register(
        nameof(ActionContent),
        typeof(object),
        typeof(SettingRow),
        new PropertyMetadata(null));

    /// <summary>Dependency property backing <see cref="DetailContent"/>.</summary>
    public static readonly DependencyProperty DetailContentProperty = DependencyProperty.Register(
        nameof(DetailContent), typeof(object), typeof(SettingRow), new PropertyMetadata(null));

    /// <summary>Initializes the settings row component.</summary>
    public SettingRow()
    {
        InitializeComponent();
        UpdateOptionalParts();
    }

    private static void OnPresentationPropertyChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        ((SettingRow)sender).UpdateOptionalParts();
    }

    /// <summary>Collapses the glyph and description slots when they carry no content so text stays vertically centered.</summary>
    private void UpdateOptionalParts()
    {
        if (HeaderIcon is null || DescriptionTextBlock is null)
        {
            return;
        }

        HeaderIcon.Visibility = string.IsNullOrEmpty(Icon) ? Visibility.Collapsed : Visibility.Visible;
        DescriptionTextBlock.Visibility = string.IsNullOrEmpty(Description) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void Layout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LayoutRoot is null || ActionContentPresenter is null || LayoutRoot.ActualWidth <= 0)
        {
            return;
        }

        // Use the row's available space and the actual localized controls, not a window breakpoint.
        double iconWidth = HeaderIcon.Visibility == Visibility.Visible
            ? HeaderIcon.ActualWidth + HeaderIcon.Margin.Left + HeaderIcon.Margin.Right
            : 0;
        double contentWidth = LayoutRoot.ActualWidth
            - LayoutRoot.Padding.Left - LayoutRoot.Padding.Right
            - LayoutRoot.BorderThickness.Left - LayoutRoot.BorderThickness.Right
            - iconWidth;
        double actionWidth = ActionContentPresenter.DesiredSize.Width;
        bool actionsBelow = actionWidth > 0
            && contentWidth - actionWidth < MinimumTextColumnWidth;
        if (_actionsBelow == actionsBelow)
        {
            return;
        }

        _actionsBelow = actionsBelow;
        Grid.SetColumnSpan(TextPanel, actionsBelow ? 2 : 1);
        Grid.SetRow(ActionContentPresenter, actionsBelow ? 1 : 0);
        Grid.SetColumn(ActionContentPresenter, actionsBelow ? 1 : 2);
        Grid.SetColumnSpan(ActionContentPresenter, actionsBelow ? 2 : 1);
        ActionContentPresenter.HorizontalAlignment = actionsBelow ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        ActionContentPresenter.Margin = actionsBelow ? new Thickness(0, 12, 0, 0) : new Thickness(16, 0, 0, 0);
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

    /// <summary>Gets or sets the Segoe Fluent Icons glyph shown before the row text; empty hides the icon slot.</summary>
    public string Icon
    {
        get => (string)GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    /// <summary>Gets or sets the action control displayed beside or below the row text.</summary>
    public object? ActionContent
    {
        get => GetValue(ActionContentProperty);
        set => SetValue(ActionContentProperty, value);
    }

    /// <summary>Gets or sets expanded feedback below the row's text and actions.</summary>
    public object? DetailContent
    {
        get => GetValue(DetailContentProperty);
        set => SetValue(DetailContentProperty, value);
    }
}
