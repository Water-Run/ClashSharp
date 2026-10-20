using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class ConnectionsViewModelTests
{
    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_PreservesTheConnectionFilterAndDoesNotFetchOrClose(AppLanguage language)
    {
        MutablePageLocalization localization = new() { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        FakeConnectionClient client = new()
        {
            Connections = [new("one", "unknown", "example.test", "rule", "payload", "proxy", 10, 20, DateTimeOffset.UnixEpoch)],
        };
        ConnectionsViewModel page = new(localization, client, new FakeConnectionLog(), new TestApplicationErrorSink());
        await page.RefreshConnectionsAsync(CancellationToken.None);
        page.SearchText = "example.test";
        ActiveConnectionDisplayRow selectedRow = Assert.Single(page.Connections);
        var original = page.Connections.Select(row => row.Connection).ToArray();
        int reads = client.RefreshCount;
        localization.Language = language;

        page.RefreshLocalization();

        Assert.Equal("example.test", page.SearchText);
        Assert.Equal(original, page.Connections.Select(row => row.Connection));
        Assert.Same(selectedRow, Assert.Single(page.Connections));
        Assert.Equal(localization.GetString("Connections.Process.Unknown"), selectedRow.ProcessNameDisplay);
        Assert.Equal("unknown", selectedRow.Connection.ProcessName);
        Assert.Equal(localization.GetString("Command.CloseAll"), page.CloseAllConnectionsText);
        Assert.Equal(reads, client.RefreshCount);
        Assert.Null(client.ClosedConnectionId);
        Assert.False(client.CloseAllCalled);
    }
}
