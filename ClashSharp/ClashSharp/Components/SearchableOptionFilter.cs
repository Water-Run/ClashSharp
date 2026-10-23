using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace ClashSharp.Components;

/// <summary>Updates search results without resetting retained option objects or changing their draft selection.</summary>
internal static class SearchableOptionFilter
{
    public static void Refresh(
        ObservableCollection<SearchableOptionItem> visible,
        IReadOnlyList<SearchableOptionItem> options,
        string query)
    {
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(query);
        query = query.Trim();
        SearchableOptionItem[] matches = options.Where(option => Matches(option, query)).ToArray();
        HashSet<SearchableOptionItem> retained = new(matches);
        for (int index = visible.Count - 1; index >= 0; index--)
        {
            if (!retained.Contains(visible[index])) { visible.RemoveAt(index); }
        }
        for (int index = 0; index < matches.Length; index++)
        {
            SearchableOptionItem option = matches[index];
            if (index < visible.Count && ReferenceEquals(visible[index], option)) { continue; }
            int previous = visible.IndexOf(option);
            if (previous >= 0) { visible.Move(previous, index); }
            else { visible.Insert(index, option); }
        }
    }

    private static bool Matches(SearchableOptionItem option, string query) =>
        query.Length == 0
        || option.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || option.Metadata.Contains(query, StringComparison.CurrentCultureIgnoreCase)
        || option.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase);
}
