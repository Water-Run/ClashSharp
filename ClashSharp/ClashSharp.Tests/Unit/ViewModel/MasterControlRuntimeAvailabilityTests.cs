using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    private static readonly string[] SummaryTileIds =
    [
        "memory-usage", "profile-count", "subscription-count", "proxy-node-count", "rule-count",
        "trigger-count", "system-log-count", "connection-records", "traffic-total", "traffic-snapshots",
        "node-health-records", "startup-restore-fallback", "core-config-file", "subscription-usage",
        "subscription-expiry", "profile-updated",
    ];

    [Fact]
    public async Task RuntimeSummaryTiles_BeforeFirstSnapshot_DoNotInventZeroCountsOrLocalSubscriptions()
    {
        TaskCompletionSource<MasterControlRuntimeSnapshot> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterRuntime runtime = new() { SnapshotTask = pending.Task };
        MasterControlViewModel viewModel = CreateViewModel(runtime: runtime);
        Task load = viewModel.LoadAsync(CancellationToken.None);
        try
        {
            Assert.False(load.IsCompleted);
            AssertSummaryTilesUnavailable(viewModel);
        }
        finally
        {
            pending.TrySetResult(MasterControlRuntimeSnapshot.Unavailable);
            await load.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task RuntimeSummaryTiles_UnavailableSnapshotCannotPublishResidualCountsOrOwnership()
    {
        FakeMasterSettings settings = new() { ActiveProfileId = "profile-a" };
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() with { IsAvailable = false } };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, runtime: runtime);

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.SetHeroStatusSlotAsync(0, MasterHeroStatusItemKind.TotalTraffic, CancellationToken.None);

        AssertSummaryTilesUnavailable(viewModel);
        Assert.Equal("Unavailable", Tile(viewModel, "core").Value);
        Assert.Equal("Unavailable", Tile(viewModel, "core-owner").Value);
        Assert.Equal("Unknown", Tile(viewModel, "mihomo-service").Value);
        Assert.Equal("Unavailable", viewModel.TransparentProxyStatusText);
        Assert.Equal("Unavailable", viewModel.HeroStatusItems[0].Value);
        Assert.Equal(settings.ActiveProfileId, Tile(viewModel, "active-profile").Value);
    }

    [Fact]
    public async Task RuntimeSummaryTiles_SuccessfullyReadZeroCountsRemainZeroAndLocalProfileRemainsLocal()
    {
        FakeMasterSettings settings = new() { ActiveProfileId = "local-profile" };
        MasterControlRuntimeSnapshot empty = new(
            new CoreConfigurationState("root", "config.yaml", true), 0, 0, 0, 0, 0, 0,
            new LogStorageSummary("logs.sqlite3", 0, 0, 0), new TrafficStatisticsSummary(0, 0, 0, 0, 0, 0, 0, 0),
            new MihomoServiceStatus(false, false, string.Empty), new StartupRestoreFallbackStatus(false, string.Empty),
            ActiveProfileId: settings.ActiveProfileId, ActiveProfileName: "Local profile");
        FakeMasterRuntime runtime = new() { Snapshot = empty };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, runtime: runtime);

        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.SetHeroStatusSlotAsync(0, MasterHeroStatusItemKind.TotalTraffic, CancellationToken.None);

        Assert.True(empty.IsAvailable);
        Assert.False(MasterControlRuntimeSnapshot.Unavailable.IsAvailable);
        foreach (string id in SummaryTileIds.Take(11).Where(id => id is not ("memory-usage" or "traffic-total" or "trigger-count")))
        {
            Assert.Equal("0", Tile(viewModel, id).Value);
        }
        Assert.Equal("0 B", Tile(viewModel, "memory-usage").Value);
        Assert.Equal("0 B", Tile(viewModel, "traffic-total").Value);
        Assert.Equal("0/0", Tile(viewModel, "trigger-count").Value);
        Assert.Equal("Not registered", Tile(viewModel, "startup-restore-fallback").Value);
        Assert.Equal("Available", Tile(viewModel, "core-config-file").Value);
        Assert.Equal("Master.Subscription.Local", Tile(viewModel, "subscription-usage").Value);
        Assert.Equal("Master.Subscription.Local", Tile(viewModel, "subscription-expiry").Value);
        Assert.Equal("0 B", viewModel.HeroStatusItems[0].Value);
    }

    [Theory]
    [InlineData("io")]
    [InlineData("invalid-state")]
    [InlineData("access")]
    [InlineData("storage")]
    [InlineData("timeout")]
    [InlineData("transport-cancellation")]
    public async Task RuntimeSummaryTiles_FailedReadClearsOldDataAndNextSuccessfulReadRecovers(string failureKind)
    {
        FakeMasterSettings settings = new() { ActiveProfileId = "profile-a" };
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, runtime: runtime,
            getRuntimeTrafficAsync: _ => Task.FromResult(new RuntimeTrafficRateSnapshot(1024, 2048, 3, 4096, 8192)));
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.SetHeroStatusSlotAsync(0, MasterHeroStatusItemKind.TotalTraffic, CancellationToken.None);
        Assert.Equal("4", Tile(viewModel, "profile-count").Value);
        Assert.Equal("3 KB", viewModel.HeroStatusItems[0].Value);

        Exception failure = failureKind switch
        {
            "io" => new IOException("private storage detail"),
            "invalid-state" => new InvalidOperationException("private runtime detail"),
            "access" => new UnauthorizedAccessException("private access detail"),
            "storage" => new MasterControlRuntimeUnavailableException(new IOException("private database detail")),
            "timeout" => new TimeoutException("private probe detail"),
            _ => new OperationCanceledException("transport canceled without a page cancellation"),
        };
        runtime.GetSnapshotAsyncHandler = _ => Task.FromException<MasterControlRuntimeSnapshot>(failure);
        viewModel.InvalidateAfterAction();
        await viewModel.LoadAsync(CancellationToken.None);

        AssertSummaryTilesUnavailable(viewModel);
        Assert.Equal("Unavailable", viewModel.HeroStatusItems[0].Value);
        Assert.Equal("1 KB/s", Tile(viewModel, "upload-rate").Value);
        Assert.Equal("3", Tile(viewModel, "active-connections").Value);
        Assert.Equal("12 KB", Tile(viewModel, "session-traffic").Value);
        Assert.Equal("profile-a", Tile(viewModel, "active-profile").Value);

        runtime.GetSnapshotAsyncHandler = null;
        runtime.Snapshot = runtime.Snapshot with { ProfileCount = 5, ActiveProfileName = "Recovered profile" };
        viewModel.InvalidateAfterAction();
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("5", Tile(viewModel, "profile-count").Value);
        Assert.Equal("Recovered profile", Tile(viewModel, "profile-count").Detail);
        Assert.Equal("3 KB", viewModel.HeroStatusItems[0].Value);
        Assert.Equal("Registered", Tile(viewModel, "startup-restore-fallback").Value);
        Assert.Equal("3 KB / 8 KB", Tile(viewModel, "subscription-usage").Value);
    }

    [Fact]
    public async Task RuntimeSummaryTiles_PageCancellationDoesNotPublishALateUnavailableResult()
    {
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() };
        MasterControlViewModel viewModel = CreateViewModel(runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        TaskCompletionSource<MasterControlRuntimeSnapshot> pending = new(TaskCreationOptions.RunContinuationsAsynchronously);
        runtime.SnapshotTask = pending.Task;
        viewModel.InvalidateAfterAction();
        using CancellationTokenSource cancellation = new();

        Task load = viewModel.LoadAsync(cancellation.Token);
        cancellation.Cancel();
        pending.SetResult(MasterControlRuntimeSnapshot.Unavailable);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => load.WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Equal("4", Tile(viewModel, "profile-count").Value);
        Assert.Equal("3 KB", Tile(viewModel, "traffic-total").Value);
    }

    [Fact]
    public async Task SubscriptionTiles_ProfileChangeWaitsForMatchingSummaryBeforeClaimingALocalProfile()
    {
        FakeMasterSettings settings = new() { ActiveProfileId = "profile-a" };
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("3 KB / 8 KB", Tile(viewModel, "subscription-usage").Value);

        settings.ActiveProfileId = "profile-b";
        await viewModel.LoadAsync(CancellationToken.None);

        foreach (string id in SummaryTileIds.TakeLast(3))
        {
            Assert.Equal("Unavailable", Tile(viewModel, id).Value);
            Assert.Empty(Tile(viewModel, id).Detail);
        }
        Assert.Equal("profile-b", Tile(viewModel, "active-profile").Value);

        runtime.Snapshot = runtime.Snapshot with { ActiveProfileId = "profile-b", ActiveProfileName = "Local B", ActiveSubscription = null };
        viewModel.InvalidateAfterAction();
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Master.Subscription.Local", Tile(viewModel, "subscription-usage").Value);
        Assert.Equal("Local B", Tile(viewModel, "subscription-usage").Detail);
    }

    private static void AssertSummaryTilesUnavailable(MasterControlViewModel viewModel)
    {
        foreach (string id in SummaryTileIds)
        {
            Assert.Equal("Unavailable", Tile(viewModel, id).Value);
            Assert.Empty(Tile(viewModel, id).Detail);
        }
    }

    [Theory]
    [InlineData("proxy-node-count", "5", "2")]
    [InlineData("rule-count", "6", "3")]
    public async Task RuntimeSummaryTiles_ProfileChangeDoesNotAttachPreviousProfileCountsToTheNewSelection(
        string tileId, string originalCount, string refreshedCount)
    {
        FakeMasterSettings settings = new() { ActiveProfileId = "profile-a" };
        FakeMasterRuntime runtime = new() { Snapshot = CreateAvailableSummaryForTiles() };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal(originalCount, Tile(viewModel, tileId).Value);

        settings.ActiveProfileId = "profile-b";
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Unavailable", Tile(viewModel, tileId).Value);
        Assert.Equal("4", Tile(viewModel, "profile-count").Value);

        runtime.Snapshot = runtime.Snapshot with { ActiveProfileId = "profile-b", ProxyNodeCount = 2, RuleCount = 3 };
        viewModel.InvalidateAfterAction();
        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal(refreshedCount, Tile(viewModel, tileId).Value);
    }

    private static MasterControlRuntimeSnapshot CreateAvailableSummaryForTiles() => new(
        new CoreConfigurationState("root", "config.yaml", true), 4, 2, 5, 6, 3, 2,
        new LogStorageSummary("logs.sqlite3", 4096, 10, 20), new TrafficStatisticsSummary(1024, 2048, 20, 2, 4, 5, 6, 7),
        new MihomoServiceStatus(true, true, string.Empty), new StartupRestoreFallbackStatus(true, "restore-command"),
        AppWorkingSetBytes: 4096, RuntimeOwnershipKnown: true, EffectiveOwner: MihomoCoreOwner.Service,
        TunRequested: true, TunEffective: true, ActiveProfileId: "profile-a", ActiveProfileName: "Profile A",
        ActiveSubscription: new ProfileSubscriptionLink("link-a", "Provider A", "https://example.invalid/sub", true, 24,
            DateTimeOffset.UnixEpoch, "ok", Usage: new SubscriptionUsage(1024, 2048, 8192, 0)),
        ActiveProfileUpdatedAt: DateTimeOffset.UnixEpoch.AddDays(1));
}
