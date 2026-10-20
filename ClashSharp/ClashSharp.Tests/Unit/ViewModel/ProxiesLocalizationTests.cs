using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class ProxiesViewModelTests
{
    [Theory]
    [MemberData(nameof(PageLocalizationTestData.Languages), MemberType = typeof(PageLocalizationTestData))]
    public async Task LocalizationRefresh_RetainsRuntimeRowsWithoutSelectingOrUpdatingAProxy(AppLanguage language)
    {
        MutablePageLocalization localization = new() { Language = language == AppLanguage.English ? AppLanguage.SimplifiedChinese : AppLanguage.English };
        FakeProxyCatalog catalog = new() { Nodes = [new("DIRECT", "DIRECT", new RegionMetadata("US", "United States", "us"), null)] };
        FakeProxyRuntimeController runtime = new() { ProxyGroups = [new("group", "Selector", "Node A", ["Node A", "Node B"])] };
        ProxiesViewModel page = new(localization, catalog, new FakeProxyLatency(), runtime, new FakeProxiesLog(), new TestApplicationErrorSink(), new ModelDisplayMapper(text => text));
        await page.LoadAsync(CancellationToken.None);
        ProxyNodeDisplay node = Assert.Single(page.ProxyNodes);
        MihomoProxyGroupDisplay group = Assert.Single(page.ProxyGroups);
        var selection = runtime.LastSelection;
        int reads = catalog.ReadCount;
        int refreshes = runtime.RefreshCount;
        localization.Language = language;

        page.RefreshLocalization();

        Assert.Same(node, Assert.Single(page.ProxyNodes));
        Assert.Same(group, Assert.Single(page.ProxyGroups));
        Assert.Equal(localization.GetString("ProxyNodes.Latency.Direct"), node.LatencyDisplay);
        Assert.Equal(localization.GetString("ProxyNodes.GroupType.Selector"), group.TypeDisplay);
        Assert.Equal("Node A", group.CurrentSelection);
        Assert.Equal(selection, runtime.LastSelection);
        Assert.Null(runtime.LastUpdatedProvider);
        Assert.Equal(reads, catalog.ReadCount);
        Assert.Equal(refreshes, runtime.RefreshCount);
    }
}
