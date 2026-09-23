using ClashSharp.Infrastructure.Networking;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Fact]
    public async Task UpdateTile_ChecksOnlyOnRequestAndRecordsFailureInsteadOfOldSuccess()
    {
        DashboardUpdateChecker checker = new();
        MasterControlViewModel viewModel = CreateViewModel(updateChecker: checker);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal(0, checker.Calls);
        await viewModel.CheckUpdatesAsync(CancellationToken.None);
        Assert.Equal(1, checker.Calls);
        Assert.Equal("About.Update.Current", Tile(viewModel, "app-update").Value);
        Assert.NotEqual("Master.Diagnostics.NotTested", Tile(viewModel, "app-update").Detail);
        checker.Fail = true;
        await viewModel.CheckUpdatesAsync(CancellationToken.None);
        Assert.Equal("About.Update.Unavailable", Tile(viewModel, "app-update").Value);
        Assert.DoesNotContain("private", viewModel.UpdateDetails);
    }

    private sealed class DashboardUpdateChecker : IApplicationUpdateChecker
    {
        public string CurrentVersion => "1.0.0";
        public int Calls { get; private set; }
        public bool Fail { get; set; }
        public Task<ApplicationUpdateCheckResult> CheckAsync(CancellationToken cancellationToken)
        {
            Calls++;
            if (Fail) { throw new HttpRequestException("private failure"); }
            return Task.FromResult(ApplicationUpdateCheckResult.Current());
        }
    }

    [Fact]
    public async Task DiagnosticTiles_LoadDoesNotContactWebsitesOrIpProvider()
    {
        MasterControlViewModel viewModel = CreateViewModel(
            probeWebsiteAsync: (_, _) => throw new InvalidOperationException("Unexpected website request"),
            probePublicIpAsync: _ => throw new InvalidOperationException("Unexpected IP request"));
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Master.Diagnostics.NotTested", Tile(viewModel, "public-ip").Value);
        Assert.Equal("Master.Diagnostics.NotTested", Tile(viewModel, "connection-test").Value);
    }

    [Fact]
    public async Task DiagnosticTiles_WebsitesRetainPartialFailureAndClearAfterRouteChange()
    {
        FakeMasterSettings settings = new();
        int requests = 0;
        MasterControlViewModel viewModel = CreateViewModel(settings: settings,
            probeWebsiteAsync: (url, _) => Task.FromResult(++requests switch
            {
                1 => new WebsiteProbeResult(url, 204, 12, null),
                2 => new WebsiteProbeResult(url, 403, 25, null),
                _ => new WebsiteProbeResult(url, null, null, "timeout"),
            }));
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.CheckWebsitesAsync(CancellationToken.None);
        Assert.Equal(3, requests);
        Assert.Equal("HTTP 204 · 12 ms", Tile(viewModel, "connection-test-proxy-url-1").Value);
        Assert.Equal("HTTP 403 · 25 ms", Tile(viewModel, "connection-test-proxy-url-2").Value);
        Assert.Equal("Master.Diagnostics.Timeout", Tile(viewModel, "connection-test-direct-url").Value);
        Assert.Contains("HTTP 403", viewModel.WebsiteDetails);
        settings.ActiveProfileId = "another-profile";
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Master.Diagnostics.NotTested", viewModel.WebsiteDetails);
        Assert.Equal("Master.Diagnostics.NotTested", Tile(viewModel, "connection-test").Value);
    }

    [Fact]
    public async Task DiagnosticTiles_InFlightOldRouteResultsAreDiscarded()
    {
        FakeMasterSettings settings = new();
        TaskCompletionSource<PublicIpInformation> pending = new();
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, probePublicIpAsync: _ => pending.Task);
        await viewModel.LoadAsync(CancellationToken.None);
        Task check = viewModel.RefreshPublicIpAsync(CancellationToken.None);
        Assert.Equal("Master.Diagnostics.Testing", Tile(viewModel, "public-ip").Value);
        settings.CurrentMode = ClashSharpMode.RuleTakeover;
        pending.SetResult(new("203.0.113.1", "City", "AS64500", "ISP", "Org", "Etc/UTC"));
        await check;
        Assert.Equal("Master.Diagnostics.NotTested", Tile(viewModel, "public-ip").Value);
        Assert.DoesNotContain("203.0.113.1", viewModel.PublicIpDetails);
    }

    [Fact]
    public async Task DiagnosticTiles_FailedIpRefreshDoesNotKeepOldAddress()
    {
        bool fail = false;
        MasterControlViewModel viewModel = CreateViewModel(probePublicIpAsync: _ => fail
            ? throw new HttpRequestException("Private diagnostic")
            : Task.FromResult(new PublicIpInformation("203.0.113.1", "City", "AS64500", "ISP", "Org", "Etc/UTC")));
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.RefreshPublicIpAsync(CancellationToken.None);
        Assert.Contains("Org", viewModel.PublicIpDetails);
        Assert.Contains("Etc/UTC", viewModel.PublicIpDetails);
        fail = true;
        await viewModel.RefreshPublicIpAsync(CancellationToken.None);
        Assert.Equal("Master.Diagnostics.Failed", Tile(viewModel, "public-ip").Value);
        Assert.DoesNotContain("Private", viewModel.PublicIpDetails);
        Assert.DoesNotContain("203.0.113.1", viewModel.PublicIpDetails);
    }

    [Fact]
    public async Task RuntimeTiles_ShowMemoryUptimeAndClearUnavailableRuntime()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        RuntimeTrafficRateSnapshot traffic = new(0, 0, 0, 0, 0, 52428800, now.AddSeconds(-90));
        MasterControlViewModel viewModel = CreateViewModel(getNow: () => now,
            getRuntimeTrafficAsync: _ => Task.FromResult(traffic));
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("50 MB", Tile(viewModel, "core-memory").Value);
        Assert.Equal("0.00:01:30", Tile(viewModel, "core-uptime").Value);
        traffic = default;
        now = now.AddSeconds(1);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Unavailable", Tile(viewModel, "core-memory").Value);
        Assert.Equal("Unavailable", Tile(viewModel, "core-uptime").Value);
    }

    [Fact]
    public async Task SubscriptionTiles_DoNotInventMissingMetadataOrWrapLargeTotals()
    {
        FakeMasterSettings settings = new() { ActiveProfileId = "subscription-link" };
        ProfileSubscriptionLink link = new("link", "Provider", "https://example.invalid/sub", true, 24,
            DateTimeOffset.UtcNow, "ok", Usage: new(long.MaxValue, long.MaxValue, 0, 0));
        FakeMasterRuntime runtime = new()
        {
            Snapshot = MasterControlRuntimeSnapshot.Unavailable with
            {
                ActiveProfileId = settings.ActiveProfileId,
                ActiveSubscription = link,
                RuntimeTraffic = new(0, 0, 0, long.MaxValue, long.MaxValue),
            },
        };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("16 EB / 0 B", Tile(viewModel, "subscription-usage").Value);
        Assert.Equal("16 EB", Tile(viewModel, "session-traffic").Value);
        Assert.Equal("Links.Metadata.NoExpiry", Tile(viewModel, "subscription-expiry").Value);
        runtime.Snapshot = runtime.Snapshot with { ActiveSubscription = link with { Usage = new(null, 100, null, null) } };
        viewModel.InvalidateAfterAction();
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Links.Metadata.NotProvided / Links.Metadata.NotProvided", Tile(viewModel, "subscription-usage").Value);
        settings.ActiveProfileId = "builtin-direct";
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Master.Subscription.Local", Tile(viewModel, "subscription-usage").Value);
    }

    private static MasterControlInfoTileViewModel Tile(MasterControlViewModel viewModel, string id) => viewModel.InfoTiles.Single(tile => tile.Id == id);

    [Fact]
    public async Task TrafficTrends_AreBoundedAndDoNotConnectAcrossSamplingGaps()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        long rate = 0;
        MasterControlViewModel viewModel = CreateViewModel(getNow: () => now,
            getRuntimeTrafficAsync: _ => Task.FromResult(new RuntimeTrafficRateSnapshot(rate, rate * 2, 0, 0, 0)));
        for (int index = 0; index < 70; index++)
        {
            now = now.AddSeconds(1);
            rate = index;
            await viewModel.LoadAsync(CancellationToken.None);
        }
        Assert.Equal(60, Tile(viewModel, "upload-rate").History.Length);
        Assert.Equal(69, Tile(viewModel, "upload-rate").History[^1]);
        Assert.Equal(138, Tile(viewModel, "download-rate").History[^1]);
        now = now.AddMinutes(1);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Single(Tile(viewModel, "upload-rate").History);
    }

    [Fact]
    public async Task DiagnosticCancellation_UnwindsAndAllowsRetry()
    {
        bool cancel = true;
        MasterControlViewModel viewModel = CreateViewModel(probePublicIpAsync: async token =>
        {
            if (cancel) { await Task.Delay(Timeout.Infinite, token); }
            return new("203.0.113.1", "City", "AS64500", "ISP", "Org", "Etc/UTC");
        });
        await viewModel.LoadAsync(CancellationToken.None);
        using CancellationTokenSource lifetime = new();
        Task operation = viewModel.RefreshPublicIpAsync(lifetime.Token);
        lifetime.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal("Master.Diagnostics.NotTested", Tile(viewModel, "public-ip").Value);
        cancel = false;
        await viewModel.RefreshPublicIpAsync(CancellationToken.None);
        Assert.Equal("203.0.113.1", Tile(viewModel, "public-ip").Value);
    }
}
