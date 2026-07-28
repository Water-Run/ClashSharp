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
    public static readonly DependencyProperty SearchPlaceholderProperty = DependencyProperty.Register(
        nameof(SearchPlaceholder),
        typeof(string),
        typeof(SearchableOptionList),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty MaxListHeightProperty = DependencyProperty.Register(
        nameof(MaxListHeight),
        typeof(double),
        typeof(SearchableOptionList),
        new PropertyMetadata(360d));

    private readonly List<SearchableOptionItem> _allOptions = [];

    public SearchableOptionList()
    {
        InitializeComponent();
    }

    public event EventHandler? SelectionChanged;

    public ObservableCollection<SearchableOptionItem> FilteredOptions { get; } = [];

    public bool AllowMultiple { get; set; }

    public string SearchPlaceholder
    {
        get => (string)GetValue(SearchPlaceholderProperty);
        set => SetValue(SearchPlaceholderProperty, value);
    }

    public double MaxListHeight
    {
        get => (double)GetValue(MaxListHeightProperty);
        set => SetValue(MaxListHeightProperty, value);
    }

    public IReadOnlyList<SearchableOptionItem> SelectedOptions => _allOptions.Where(static option => option.IsChecked).ToList();

    public IReadOnlyList<SearchableOptionItem> Options => _allOptions.ToList();

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
