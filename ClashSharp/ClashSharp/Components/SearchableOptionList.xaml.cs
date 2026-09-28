using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Reusable searchable dialog list for selectable option rows.</summary>
public sealed partial class SearchableOptionList : UserControl
{
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

    private readonly List<SearchableOptionItem> _allOptions = [];
    private bool _allowMultiple;
    private bool _synchronizingSelection;

    /// <summary>Initializes an empty searchable option list.</summary>
    public SearchableOptionList()
    {
        InitializeComponent();
    }

    /// <summary>Occurs after the selected option set changes.</summary>
    public event EventHandler? SelectionChanged;

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
        AutomationProperties.SetName(e.ItemContainer, option?.Title ?? string.Empty);
        AutomationProperties.SetHelpText(e.ItemContainer, option?.Description ?? string.Empty);
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

    private void RefreshFilteredOptions()
    {
        string query = SearchBox?.Text?.Trim() ?? string.Empty;
        // Filtering recreates native containers, but must never edit the selected option set.
        _synchronizingSelection = true;
        try
        {
            FilteredOptions.Clear();
            foreach (SearchableOptionItem option in _allOptions)
            {
                if (Matches(option, query))
                {
                    FilteredOptions.Add(option);
                }
            }
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

    private static bool Matches(SearchableOptionItem option, string query)
    {
        return query.Length == 0
            || option.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || option.Metadata.Contains(query, StringComparison.CurrentCultureIgnoreCase)
            || option.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase);
    }
}
