extern alias ClashSharpUi;
using SearchableOptionItem = ClashSharpUi::ClashSharp.Components.SearchableOptionItem;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class SearchableOptionItemTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DisplayRefresh_PreservesSelectionIdentityAndPayload(bool selected)
    {
        object payload = new();
        SearchableOptionItem item = new("status", "Status", "Old metadata", "Old description", "glyph", payload, selected);
        List<string?> notifications = [];
        item.PropertyChanged += (_, args) =>
        {
            notifications.Add(args.PropertyName);
            Assert.Equal("Zustand", item.Title);
            Assert.Equal("New metadata", item.Metadata);
            Assert.Equal("New description", item.Description);
            Assert.Equal(selected, item.IsChecked);
        };

        item.UpdateDisplay("Zustand", "New metadata", "New description");

        Assert.Collection(notifications,
            value => Assert.Equal("Title", value),
            value => Assert.Equal("Metadata", value),
            value => Assert.Equal("Description", value));
        Assert.Equal("status", item.Id);
        Assert.Equal("glyph", item.Glyph);
        Assert.Same(payload, item.Payload);
    }

    [Fact]
    public void UnchangedDisplay_DoesNotNotify()
    {
        SearchableOptionItem first = new("first", "First", "", "", "", isChecked: true);
        int notifications = 0;
        first.PropertyChanged += (_, _) => notifications++;

        first.UpdateDisplay("First", "", "");

        Assert.Equal(0, notifications);
        Assert.True(first.IsChecked);
    }

    [Fact]
    public void InvalidDisplay_DoesNotPartiallyOverwriteTheCurrentLabels()
    {
        SearchableOptionItem item = new("status", "Title", "Metadata", "Description", "");

        Assert.Throws<ArgumentNullException>(() => item.UpdateDisplay("New title", "New metadata", null!));

        Assert.Equal("Title", item.Title);
        Assert.Equal("Metadata", item.Metadata);
        Assert.Equal("Description", item.Description);
    }
}
