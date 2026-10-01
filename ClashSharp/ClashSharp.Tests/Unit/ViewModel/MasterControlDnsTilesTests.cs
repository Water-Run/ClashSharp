using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    private static readonly string[] DnsTileIds = ["dns-mode", "dns-listen", "dns-ipv6", "dns-upstream", "dns-fallback", "dns-bootstrap"];
    private static RuntimeDnsConfiguration DnsForTiles() => RuntimeDnsConfigurationReader.Read("""
        dns:
          enable: true
          enhanced-mode: fake-ip
          ipv6: true
          nameserver: ['https://dns.example/dns-query', 'tls://1.1.1.1:853']
          default-nameserver: ['223.5.5.5']
          direct-nameserver: ['192.0.2.1']
        """)!;

    [Fact]
    public async Task DnsTiles_ShowCurrentRuntimeValuesAndNavigateToProfiles()
    {
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() with { Dns = DnsForTiles() } };
        MasterControlViewModel viewModel = CreateViewModel(settings: new() { ActiveProfileId = "profile-a" }, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("fake-ip", Tile(viewModel, "dns-mode").Value);
        Assert.Equal("Master.Dns.NotListening", Tile(viewModel, "dns-listen").Value);
        Assert.Equal("On", Tile(viewModel, "dns-ipv6").Value);
        Assert.Equal("2", Tile(viewModel, "dns-upstream").Value);
        Assert.Contains("https://dns.example", Tile(viewModel, "dns-upstream").Detail);
        Assert.Equal("0", Tile(viewModel, "dns-fallback").Value);
        Assert.Equal("1", Tile(viewModel, "dns-bootstrap").Value);
        foreach (string id in DnsTileIds)
        {
            Assert.False(Tile(viewModel, id).IsToggleVisible);
            Assert.Equal("Profiles", MasterControlTileNavigationCatalog.Resolve(id)!.Tag);
        }
    }

    [Theory]
    [InlineData("unavailable", "Unavailable")]
    [InlineData("owner", "Unavailable")]
    [InlineData("profile", "Unavailable")]
    [InlineData("stopped", "Off")]
    public async Task DnsTiles_DoNotReuseOldConfigurationAfterRuntimeBecomesUncertain(string change, string expected)
    {
        FakeMasterSettings settings = new() { ActiveProfileId = "profile-a" };
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() with { Dns = DnsForTiles() } };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("fake-ip", Tile(viewModel, "dns-mode").Value);
        runtime.Snapshot = change switch
        {
            "unavailable" => runtime.Snapshot with { IsAvailable = false },
            "owner" => runtime.Snapshot with { RuntimeOwnershipKnown = false },
            "stopped" => runtime.Snapshot with { EffectiveOwner = MihomoCoreOwner.None },
            _ => runtime.Snapshot with { ActiveProfileId = "profile-b" },
        };
        viewModel.InvalidateAfterAction();
        await viewModel.LoadAsync(CancellationToken.None);
        foreach (string id in DnsTileIds)
        {
            Assert.Equal(expected, Tile(viewModel, id).Value);
            Assert.Empty(Tile(viewModel, id).Detail);
            Assert.DoesNotContain("192.0.2.1", Tile(viewModel, id).Description);
        }
    }

    [Fact]
    public async Task DnsTiles_DataReplacementClearsValuesBeforeNewSnapshotArrives()
    {
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() with { Dns = DnsForTiles() } };
        MasterControlViewModel viewModel = CreateViewModel(settings: new() { ActiveProfileId = "profile-a" }, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.InvalidateForDataChange();
        foreach (string id in DnsTileIds) { Assert.Equal("Unavailable", Tile(viewModel, id).Value); }
    }

    [Fact]
    public async Task DnsTiles_DisabledResolverUsesSystemDnsAndDoesNotAdvertiseInactiveUpstreams()
    {
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() with { Dns = DnsForTiles() with { Enabled = false } } };
        MasterControlViewModel viewModel = CreateViewModel(settings: new() { ActiveProfileId = "profile-a" }, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Master.Dns.System", Tile(viewModel, "dns-mode").Value);
        Assert.Equal("Off", Tile(viewModel, "dns-upstream").Value);
        Assert.Empty(Tile(viewModel, "dns-upstream").Detail);
    }
}
