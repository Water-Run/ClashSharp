extern alias ClashSharpUi;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SearchableOptionFilter = ClashSharpUi::ClashSharp.Components.SearchableOptionFilter;
using SearchableOptionItem = ClashSharpUi::ClashSharp.Components.SearchableOptionItem;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class SearchableOptionFilterTests
{
    [Fact]
    public void TranslatedUnfilteredRows_KeepTheirObjectsOrderAndSelectionWithoutCollectionChanges()
    {
        SearchableOptionItem first = new("first", "First", "", "", "", isChecked: true);
        SearchableOptionItem second = new("second", "Second", "", "", "");
        SearchableOptionItem[] draft = [second, first];
        ObservableCollection<SearchableOptionItem> visible = new(draft);
        int changes = 0;
        visible.CollectionChanged += (_, _) => changes++;
        first.UpdateDisplay("Erste", "", "Beschreibung");
        second.UpdateDisplay("Zweite", "", "");

        SearchableOptionFilter.Refresh(visible, draft, string.Empty);

        Assert.Equal(0, changes);
        Assert.Same(second, visible[0]);
        Assert.Same(first, visible[1]);
        Assert.True(first.IsChecked);
        Assert.False(second.IsChecked);
    }

    [Fact]
    public void Translation_ReevaluatesTheQueryWithoutLosingAHiddenSelection()
    {
        SearchableOptionItem selected = new("status", "Status", "", "", "", isChecked: true);
        SearchableOptionItem[] draft = [selected];
        ObservableCollection<SearchableOptionItem> visible = new(draft);
        selected.UpdateDisplay("状态", "", "显示运行状态");

        SearchableOptionFilter.Refresh(visible, draft, "status");

        Assert.Empty(visible);
        Assert.True(selected.IsChecked);
        SearchableOptionFilter.Refresh(visible, draft, "状态");
        Assert.Same(selected, Assert.Single(visible));
        Assert.True(selected.IsChecked);
    }

    [Fact]
    public void ChangedMatches_OnlyReplaceTheChangedRows()
    {
        SearchableOptionItem removed = new("removed", "match", "", "", "", isChecked: true);
        SearchableOptionItem retained = new("retained", "match", "", "", "");
        SearchableOptionItem added = new("added", "other", "", "", "", isChecked: true);
        SearchableOptionItem[] draft = [added, retained, removed];
        ObservableCollection<SearchableOptionItem> visible = [retained, removed];
        List<NotifyCollectionChangedAction> changes = [];
        visible.CollectionChanged += (_, args) => changes.Add(args.Action);
        removed.UpdateDisplay("other", "", "");
        added.UpdateDisplay("match", "", "");

        SearchableOptionFilter.Refresh(visible, draft, "match");

        Assert.Same(added, visible[0]);
        Assert.Same(retained, visible[1]);
        Assert.Equal(2, visible.Count);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, changes);
        Assert.Equal(2, changes.Count);
        Assert.True(removed.IsChecked);
        Assert.True(added.IsChecked);
    }

    [Fact]
    public void ReorderedDraft_MovesExistingResultsInsteadOfRecreatingThem()
    {
        SearchableOptionItem first = new("first", "First", "", "", "");
        SearchableOptionItem second = new("second", "Second", "", "", "");
        ObservableCollection<SearchableOptionItem> visible = [first, second];
        List<NotifyCollectionChangedAction> changes = [];
        visible.CollectionChanged += (_, args) => changes.Add(args.Action);

        SearchableOptionFilter.Refresh(visible, [second, first], string.Empty);

        Assert.Equal(NotifyCollectionChangedAction.Move, Assert.Single(changes));
        Assert.Same(second, visible[0]);
        Assert.Same(first, visible[1]);
    }

    [Theory]
    [InlineData("Needle", "", "")]
    [InlineData("", "Needle", "")]
    [InlineData("", "", "Needle")]
    public void Search_UsesAllDisplayFieldsAndIgnoresOuterWhitespaceAndCase(string title, string metadata, string description)
    {
        SearchableOptionItem item = new("id", title, metadata, description, "");
        ObservableCollection<SearchableOptionItem> visible = [];

        SearchableOptionFilter.Refresh(visible, [item], "  nEeDlE  ");

        Assert.Same(item, Assert.Single(visible));
        Assert.False(item.IsChecked);
    }
}
