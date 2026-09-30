using ClashSharp.Infrastructure.Networking;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Fact]
    public async Task RemainingPages_MasterReplacementRefreshesLayoutsAndSummariesWithoutTheNormalThrottle()
    {
        FakeMasterCore core = new();
        FakeMasterRuntime runtime = new() { Snapshot = MasterControlRuntimeSnapshot.Unavailable with { IsAvailable = true, ProfileCount = 7 } };
        FakeMasterHeroStatusLayoutService hero = new();
        FakeMasterInfoTileLayoutService tiles = new();
        MasterControlViewModel viewModel = CreateViewModel(core: core, runtime: runtime, heroStatusLayout: hero, infoTileLayout: tiles,
            getNow: () => DateTimeOffset.UnixEpoch,
            probeWebsiteAsync: (url, _) => Task.FromResult(new WebsiteProbeResult(url, 204, 10, null)),
            probePublicIpAsync: _ => Task.FromResult(new PublicIpInformation("203.0.113.1", "City", "AS64500", "ISP", "Org", "Etc/UTC")));
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.CheckWebsitesAsync(CancellationToken.None);
        await viewModel.RefreshPublicIpAsync(CancellationToken.None);
        Assert.Equal("7", Tile(viewModel, "profile-count").Value);

        viewModel.InvalidateForDataChange();

        Assert.Equal("Unavailable", Tile(viewModel, "profile-count").Value);
        Assert.Equal("Master.Diagnostics.NotTested", viewModel.WebsiteDetails);
        Assert.Equal("Master.Diagnostics.NotTested", viewModel.PublicIpDetails);
        runtime.Snapshot = runtime.Snapshot with { ProfileCount = 2 };
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("2", Tile(viewModel, "profile-count").Value);
        Assert.Equal(2, runtime.SnapshotCount);
        Assert.Equal(2, core.VersionProbeCount);
        Assert.Equal(2, hero.GetLayoutCount);
        Assert.Equal(2, tiles.GetLayoutCount);
    }

    [Fact]
    public async Task RemainingPages_MasterDiscardsThePreviousDirectoryRuntimeRead()
    {
        TaskCompletionSource<MasterControlRuntimeSnapshot> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterRuntime runtime = new() { SnapshotTask = result.Task };
        MasterControlViewModel viewModel = CreateViewModel(runtime: runtime);
        Task load = viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal(1, runtime.SnapshotCount);

        viewModel.InvalidateForDataChange();
        result.SetResult(MasterControlRuntimeSnapshot.Unavailable with { IsAvailable = true, ProfileCount = 99 });
        await load;

        Assert.Equal("Unavailable", Tile(viewModel, "profile-count").Value);
    }

    [Fact]
    public async Task RemainingPages_MasterDiscardsAnOldIpProbeWhenTheReplacementReusesTheSameRoute()
    {
        TaskCompletionSource<PublicIpInformation> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        MasterControlViewModel viewModel = CreateViewModel(probePublicIpAsync: _ => result.Task);
        await viewModel.LoadAsync(CancellationToken.None);
        Task probe = viewModel.RefreshPublicIpAsync(CancellationToken.None);

        viewModel.InvalidateForDataChange();
        await viewModel.LoadAsync(CancellationToken.None);
        result.SetResult(new("203.0.113.1", "City", "AS64500", "ISP", "Org", "Etc/UTC"));
        await probe;

        Assert.Equal("Master.Diagnostics.NotTested", viewModel.PublicIpDetails);
    }

    [Fact]
    public async Task RemainingPages_ModeCommandsRespectThePageInteractionOwner()
    {
        int admitted = 0;
        MasterControlViewModel viewModel = CreateViewModel(runTileOperationAsync: (_, _) =>
        {
            admitted++;
            return Task.CompletedTask;
        });

        await viewModel.RuleTakeoverModeCommand.ExecuteObservedAsync(null, CancellationToken.None);

        Assert.Equal(1, admitted);
        Assert.Equal(ClashSharpMode.Disabled, viewModel.SelectedMode);
    }
}
