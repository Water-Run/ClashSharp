using System.Windows.Input;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Theory]
    [InlineData("core", "Settings")]
    [InlineData("profile-count", "Profiles")]
    [InlineData("subscription-usage", "Links")]
    [InlineData("proxy-node-count", "ProxyNodes")]
    [InlineData("rule-count", "Rules")]
    [InlineData("trigger-count", "Triggers")]
    [InlineData("active-connections", "Connections")]
    [InlineData("system-log-count", "Logs")]
    [InlineData("traffic-total", "Statistics")]
    [InlineData("system-info", "About")]
    public async Task InformationTile_OnlyExplicitDetailLinkNavigatesWithoutReplacingItsDetails(string tileId, string expectedPage)
    {
        List<string> visited = [];
        MasterControlViewModel viewModel = CreateViewModel(navigateToPage: visited.Add);
        await viewModel.LoadAsync(CancellationToken.None);
        MasterControlInfoTileViewModel tile = Tile(viewModel, tileId);
        Assert.Empty(visited);
        Assert.Null(tile.TileCommand);
        Assert.NotEmpty(tile.NavigationText);
        ICommand command = Assert.IsAssignableFrom<ICommand>(tile.NavigationCommand);
        string originalValue = tile.Value;
        string originalDetail = tile.Detail;

        command.Execute(null);

        Assert.Equal([expectedPage], visited);
        Assert.Equal(originalValue, tile.Value);
        Assert.Equal(originalDetail, tile.Detail);
    }

    [Fact]
    public async Task InformationTile_WithoutShellNavigationDoesNotOfferAnInertLink()
    {
        MasterControlViewModel viewModel = CreateViewModel();
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.NotEmpty(viewModel.InfoTiles);
        Assert.All(viewModel.InfoTiles, tile =>
        {
            Assert.Null(tile.NavigationCommand);
            Assert.Empty(tile.NavigationText);
        });
    }

    [Fact]
    public async Task InformationTile_CommandActionsAndReadOnlyDetailsKeepTheirExistingBehavior()
    {
        List<string> visited = [];
        MasterControlViewModel viewModel = CreateViewModel(navigateToPage: visited.Add);
        await viewModel.LoadAsync(CancellationToken.None);
        MasterControlInfoTileViewModel action = Tile(viewModel, "connection-test");
        Assert.NotNull(action.TileCommand);
        Assert.Null(action.NavigationCommand);
        MasterControlInfoTileViewModel detail = Tile(viewModel, "core-memory");
        Assert.Null(detail.TileCommand);
        Assert.Null(detail.NavigationCommand);
        Assert.Empty(detail.NavigationText);
        Assert.Empty(visited);
    }
}
