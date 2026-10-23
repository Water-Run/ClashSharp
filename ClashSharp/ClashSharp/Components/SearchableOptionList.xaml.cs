using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using ClashSharp.Presentation.Dialogs;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Windows.Foundation;

namespace ClashSharp.Components;

/// <summary>Reusable searchable dialog list for selectable option rows.</summary>
public sealed partial class SearchableOptionList : UserControl
{
    /// <summary>Identifies optional controls placed alongside the search input.</summary>
    public static readonly DependencyProperty SearchLeadingContentProperty = DependencyProperty.Register(
        nameof(SearchLeadingContent), typeof(UIElement), typeof(SearchableOptionList),
        new PropertyMetadata(null, OnSearchLeadingContentChanged));

    /// <summary>Identifies the optional <see cref="SearchHeader"/> dependency property.</summary>
    public static readonly DependencyProperty SearchHeaderProperty = DependencyProperty.Register(
        nameof(SearchHeader),
        typeof(string),
        typeof(SearchableOptionList),
        new PropertyMetadata(null));

    /// <summary>Identifies the <see cref="SearchPlaceholder"/> dependency property.</summary>
    public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
        nameof(SearchPlaceholder),
        typeof(string),
        typeof(SearchableOptionList),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="EmptyText"/> dependency property.</summary>
    public static readonly DependencyProperty EmptyTextProperty = DependencyProperty.Register(
        nameof(EmptyText),
        typeof(string),
        typeof(SearchableOptionList),
        new PropertyMetadata(string.Empty));

    /// <summary>Identifies the <see cref="MaxListHeight"/> dependency property.</summary>
    public static readonly DependencyProperty MaxListHeightProperty = DependencyProperty.Register(
        nameof(MaxListHeight),
        typeof(double),
        typeof(SearchableOptionList),
        new PropertyMetadata(360d));

    /// <summary>Identifies the live move-up label used by existing native row menus.</summary>
    public static readonly DependencyProperty MoveUpTextProperty = DependencyProperty.Register(
        nameof(MoveUpText), typeof(string), typeof(SearchableOptionList), new PropertyMetadata(string.Empty));

    /// <summary>Identifies the live move-down label used by existing native row menus.</summary>
    public static readonly DependencyProperty MoveDownTextProperty = DependencyProperty.Register(
        nameof(MoveDownText), typeof(string), typeof(SearchableOptionList), new PropertyMetadata(string.Empty));

    private readonly List<SearchableOptionItem> _allOptions = [];
    private bool _allowMultiple;
    private bool _synchronizingSelection;
    private MenuFlyout? _openReorderMenu;

    /// <summary>Initializes an empty searchable option list.</summary>
    public SearchableOptionList()
    {
        InitializeComponent();
        // Callers set initial selections before the dialog loads; assign the source before synchronizing them.
        MultipleOptionsControl.ItemsSource = FilteredOptions;
        SingleOptionsControl.ItemsSource = FilteredOptions;
        Unloaded += (_, _) => WindowDialogCoordinator.TryDismissTransient(XamlRoot, () => _openReorderMenu?.Hide());
    }

    /// <summary>Occurs after the selected option set changes.</summary>
    public event EventHandler? SelectionChanged;

    /// <summary>Gets or sets an optional view-owned control beside the search input.</summary>
    public UIElement? SearchLeadingContent
    {
        get => (UIElement?)GetValue(SearchLeadingContentProperty);
        set => SetValue(SearchLeadingContentProperty, value);
    }

    private static void OnSearchLeadingContentChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        SearchableOptionList list = (SearchableOptionList)sender;
        bool visible = args.NewValue is UIElement;
        list.SearchLeadingPresenter.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        list.SearchRow.ColumnSpacing = visible ? 12 : 0;
    }

    /// <summary>Gets the observable options that match the current search text.</summary>
    public ObservableCollection<SearchableOptionItem> FilteredOptions { get; } = [];

    /// <summary>Gets or sets whether more than one option may be selected.</summary>
    public bool AllowMultiple
    {
        get => _allowMultiple;
        set
        {
            _allowMultiple = value;
            MultipleOptionsControl.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            SingleOptionsScroller.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            SynchronizeSelection();
        }
    }

    /// <summary>Gets or sets whether multiple-choice rows expose native ordering commands.</summary>
    public bool AllowReorder { get; set; }

    /// <summary>Gets or sets the localized move-up command label.</summary>
    public string MoveUpText
    {
        get => (string)GetValue(MoveUpTextProperty);
        set => SetValue(MoveUpTextProperty, value);
    }

    /// <summary>Gets or sets the localized move-down command label.</summary>
    public string MoveDownText
    {
        get => (string)GetValue(MoveDownTextProperty);
        set => SetValue(MoveDownTextProperty, value);
    }

    /// <summary>Gets or sets the optional instruction for accessing ordering commands.</summary>
    public string ReorderHint
    {
        get => ReorderHintText.Text;
        set
        {
            ReorderHintText.Text = value;
            ReorderHintText.Visibility = string.IsNullOrWhiteSpace(value) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    internal double MeasureReorderHintHeight(double width)
    {
        if (ReorderHintText.Visibility is Visibility.Collapsed)
        {
            return 0;
        }

        ReorderHintText.Measure(new Size(width, double.PositiveInfinity));
        return ReorderHintText.DesiredSize.Height + 10;
    }

    /// <summary>Gets or sets an optional selection instruction above the search field.</summary>
    public string? SearchHeader
    {
        get => (string?)GetValue(SearchHeaderProperty);
        set => SetValue(SearchHeaderProperty, value);
    }

    /// <summary>Gets or sets the localized message shown when the filter has no matches.</summary>
    public string EmptyText
    {
        get => (string)GetValue(EmptyTextProperty);
        set => SetValue(EmptyTextProperty, value);
    }

    /// <summary>Gets or sets the text shown when the search box is empty.</summary>
    public string SearchPlaceholder
    {
        get => (string)GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    /// <summary>Gets or sets the maximum height of the scrollable option region.</summary>
    public double MaxListHeight
    {
        get => (double)GetValue(MaxListHeightProperty);
        set => SetValue(MaxListHeightProperty, value);
    }

    /// <summary>Gets a snapshot of all currently selected options.</summary>
    public IReadOnlyList<SearchableOptionItem> SelectedOptions => _allOptions.Where(static option => option.IsChecked).ToList();

    /// <summary>Gets a snapshot of the complete unfiltered option set.</summary>
    public IReadOnlyList<SearchableOptionItem> Options => _allOptions.ToList();

    /// <summary>Replaces the complete option set and reapplies the current filter.</summary>
    /// <param name="options">Options to own and display in their enumeration order.</param>
    public void SetOptions(IEnumerable<SearchableOptionItem> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        foreach (SearchableOptionItem option in _allOptions)
        {
            option.PropertyChanged -= Option_PropertyChanged;
        }

        _allOptions.Clear();
        _allOptions.AddRange(options);
        foreach (SearchableOptionItem option in _allOptions)
        {
            option.PropertyChanged += Option_PropertyChanged;
        }

        RefreshFilteredOptions();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshFilteredOptions();
    }

    private void Option_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SearchableOptionItem.IsChecked))
        {
            SynchronizeSelection();
        }
    }

    private void MultipleOptionsControl_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs e)
    {
        SearchableOptionItem? option = e.InRecycleQueue ? null : e.Item as SearchableOptionItem;
        if (option is not null)
        {
            e.ItemContainer.SetBinding(AutomationProperties.NameProperty, new Binding { Source = option, Path = new PropertyPath(nameof(option.Title)), Mode = BindingMode.OneWay });
            e.ItemContainer.SetBinding(AutomationProperties.HelpTextProperty, new Binding { Source = option, Path = new PropertyPath(nameof(option.Description)), Mode = BindingMode.OneWay });
            e.ItemContainer.SetBinding(ToolTipService.ToolTipProperty, new Binding { Source = option, Path = new PropertyPath(nameof(option.Description)), Mode = BindingMode.OneWay });
        }
        else
        {
            e.ItemContainer.ClearValue(AutomationProperties.NameProperty);
            e.ItemContainer.ClearValue(AutomationProperties.HelpTextProperty);
            e.ItemContainer.ClearValue(ToolTipService.ToolTipProperty);
        }
        if (e.ItemContainer.ContextFlyout is MenuFlyout previous && ReferenceEquals(previous, _openReorderMenu))
        {
            WindowDialogCoordinator.TryDismissTransient(XamlRoot, previous.Hide);
        }
        e.ItemContainer.ContextFlyout = option is not null && AllowReorder
            ? CreateReorderMenu(option)
            : null;
    }

    private MenuFlyout CreateReorderMenu(SearchableOptionItem option)
    {
        MenuFlyout menu = new();
        MenuFlyoutItem up = new() { Icon = new FontIcon { Glyph = "\uE70E" } };
        MenuFlyoutItem down = new() { Icon = new FontIcon { Glyph = "\uE70D" } };
        up.SetBinding(MenuFlyoutItem.TextProperty, new Binding { Source = this, Path = new PropertyPath(nameof(MoveUpText)), Mode = BindingMode.OneWay });
        down.SetBinding(MenuFlyoutItem.TextProperty, new Binding { Source = this, Path = new PropertyPath(nameof(MoveDownText)), Mode = BindingMode.OneWay });
        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Opening += (_, _) =>
        {
            _openReorderMenu = menu;
            int index = FilteredOptions.IndexOf(option);
            up.IsEnabled = AllowMultiple && AllowReorder && index > 0;
            down.IsEnabled = AllowMultiple && AllowReorder && index >= 0 && index < FilteredOptions.Count - 1;
        };
        bool moved = false;
        up.Click += (_, _) => moved = MoveOption(option, -1);
        down.Click += (_, _) => moved = MoveOption(option, 1);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_openReorderMenu, menu)) { _openReorderMenu = null; }
            if (moved)
            {
                moved = false;
                // Let the flyout finish returning focus before focusing the row in its new position.
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (IsLoaded && FilteredOptions.Contains(option))
                    {
                        MultipleOptionsControl.ScrollIntoView(option);
                        MultipleOptionsControl.UpdateLayout();
                        if (MultipleOptionsControl.ContainerFromItem(option) is ListViewItem container)
                        {
                            container.Focus(FocusState.Keyboard);
                        }
                    }
                });
            }
        };
        return menu;
    }

    private bool MoveOption(SearchableOptionItem option, int direction)
    {
        int index = FilteredOptions.IndexOf(option);
        int destination = index + direction;
        if (!AllowMultiple || !AllowReorder || index < 0 || destination < 0 || destination >= FilteredOptions.Count)
        {
            return false;
        }

        // Swap adjacent search results without moving hidden rows or changing their checked state.
        int sourceIndex = _allOptions.IndexOf(option);
        int destinationIndex = _allOptions.IndexOf(FilteredOptions[destination]);
        (_allOptions[sourceIndex], _allOptions[destinationIndex]) = (_allOptions[destinationIndex], _allOptions[sourceIndex]);
        _synchronizingSelection = true;
        try
        {
            FilteredOptions.Move(index, destination);
        }
        finally
        {
            _synchronizingSelection = false;
        }

        SynchronizeSelection();
        return true;
    }

    private void MultipleOptionsControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection || !AllowMultiple)
        {
            return;
        }

        _synchronizingSelection = true;
        try
        {
            foreach (SearchableOptionItem option in e.RemovedItems.OfType<SearchableOptionItem>())
            {
                option.IsChecked = false;
            }

            foreach (SearchableOptionItem option in e.AddedItems.OfType<SearchableOptionItem>())
            {
                option.IsChecked = true;
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SingleOption_GotFocus(object sender, RoutedEventArgs e)
    {
        // RadioButtons moves arrow-key focus programmatically; keep the complete row in view.
        if (sender is RadioButton { FocusState: FocusState.Keyboard or FocusState.Programmatic } option)
        {
            // Native focus visuals extend outside the control's bounds.
            Thickness margin = option.FocusVisualMargin;
            double left = Math.Min(0, margin.Left);
            double top = Math.Min(0, margin.Top);
            option.StartBringIntoView(new BringIntoViewOptions
            {
                AnimationDesired = false,
                TargetRect = new Rect(
                    left,
                    top,
                    option.ActualWidth - left - Math.Min(0, margin.Right),
                    option.ActualHeight - top - Math.Min(0, margin.Bottom)),
            });
        }
    }

    private void SingleOptionsControl_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_synchronizingSelection || AllowMultiple
            || SingleOptionsControl.SelectedItem is not SearchableOptionItem selected)
        {
            return;
        }

        _synchronizingSelection = true;
        try
        {
            foreach (SearchableOptionItem option in _allOptions)
            {
                option.IsChecked = ReferenceEquals(option, selected);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Reapplies the current query after a batch of translated labels without rebuilding unchanged rows.</summary>
    public void RefreshFilter() => RefreshFilteredOptions();

    private void RefreshFilteredOptions()
    {
        string query = SearchBox?.Text?.Trim() ?? string.Empty;
        // Native selection changes caused by filtering must not edit the unfiltered draft.
        _synchronizingSelection = true;
        try
        {
            SearchableOptionFilter.Refresh(FilteredOptions, _allOptions, query);
        }
        finally
        {
            _synchronizingSelection = false;
        }

        SynchronizeSelection();

        if (EmptyStateText is not null)
        {
            EmptyStateText.Visibility = FilteredOptions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void SynchronizeSelection()
    {
        if (_synchronizingSelection || MultipleOptionsControl is null || SingleOptionsControl is null)
        {
            return;
        }

        _synchronizingSelection = true;
        try
        {
            if (AllowMultiple)
            {
                foreach (SearchableOptionItem option in FilteredOptions)
                {
                    bool selected = MultipleOptionsControl.SelectedItems.Contains(option);
                    if (option.IsChecked && !selected)
                    {
                        MultipleOptionsControl.SelectedItems.Add(option);
                    }
                    else if (!option.IsChecked && selected)
                    {
                        MultipleOptionsControl.SelectedItems.Remove(option);
                    }
                }
            }
            else
            {
                SingleOptionsControl.SelectedItem = FilteredOptions.FirstOrDefault(static option => option.IsChecked);
            }
        }
        finally
        {
            _synchronizingSelection = false;
        }
    }

}
