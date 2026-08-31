using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ClashSharp.Components;

/// <summary>Reusable searchable dialog list for selectable option rows.</summary>
public sealed partial class SearchableOptionList : UserControl
{
    /// <summary>Identifies the <see cref="SearchPlaceholder"/> dependency property.</summary>
    public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
        nameof(SearchPlaceholder),
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
    public bool AllowMultiple { get; set; }

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
        _allOptions.Clear();
        _allOptions.AddRange(options);
        RefreshFilteredOptions();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshFilteredOptions();
    }

    private void DialogOptionRow_SelectionInvoked(object sender, EventArgs e)
    {
        if (sender is not DialogOptionRow { Tag: SearchableOptionItem selected })
        {
            return;
        }

        if (!AllowMultiple)
        {
            foreach (SearchableOptionItem option in _allOptions)
            {
                option.IsChecked = ReferenceEquals(option, selected);
            }
        }

        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshFilteredOptions()
    {
        string query = SearchBox?.Text?.Trim() ?? string.Empty;
        FilteredOptions.Clear();
        foreach (SearchableOptionItem option in _allOptions)
        {
            if (Matches(option, query))
            {
                FilteredOptions.Add(option);
            }
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
