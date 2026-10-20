using ClashSharp.ApplicationModel.Network;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Theory]
    [InlineData(ClashSharpMode.Disabled)]
    [InlineData(ClashSharpMode.Standby)]
    [InlineData(ClashSharpMode.RuleTakeover)]
    [InlineData(ClashSharpMode.FullTakeover)]
    public async Task VerifiedMode_OverridesTheSavedTargetWithoutChangingIt(ClashSharpMode observed)
    {
        ClashSharpMode desired = observed == ClashSharpMode.FullTakeover ? ClashSharpMode.Disabled : ClashSharpMode.FullTakeover;
        FakeMasterSettings settings = new() { CurrentMode = desired };
        FakeMasterRuntime runtime = new() { Snapshot = ObservedNetwork(observed) };
        MasterControlViewModel page = CreateViewModel(settings: settings, runtime: runtime);

        await page.LoadAsync(CancellationToken.None);

        Assert.Equal(observed, page.SelectedMode);
        Assert.Equal(desired, settings.CurrentMode);
        Assert.Equal(1, new[] { page.IsDisabledModeSelected, page.IsStandbyModeSelected,
            page.IsRuleTakeoverModeSelected, page.IsFullTakeoverModeSelected }.Count(selected => selected));
        Assert.Equal(page.BasicStatusText,
            page.HeroStatusItems.Single(item => item.Kind == MasterHeroStatusItemKind.Availability).Value);
        Assert.Equal(observed == ClashSharpMode.Disabled ? "Not running" : "Running",
            page.HeroStatusItems.Single(item => item.Kind == MasterHeroStatusItemKind.CoreStatus).Value);
    }

    [Fact]
    public async Task ExternalModeChange_RefreshesTheExistingPageWithoutReloadingPreferences()
    {
        FakeMasterSettings settings = new() { CurrentMode = ClashSharpMode.Disabled };
        FakeMasterRuntime runtime = new() { Snapshot = ObservedNetwork(ClashSharpMode.Disabled) };
        MasterControlViewModel page = CreateViewModel(settings: settings, runtime: runtime);
        await page.LoadAsync(CancellationToken.None);
        MasterHeroStatusItemViewModel item = page.HeroStatusItems[0];
        runtime.Snapshot = ObservedNetwork(ClashSharpMode.RuleTakeover);
        page.InvalidateAfterAction();

        await page.LoadAsync(CancellationToken.None);

        Assert.Equal(ClashSharpMode.RuleTakeover, page.SelectedMode);
        Assert.Equal("Active", page.BasicStatusText);
        Assert.Equal(ClashSharpMode.Disabled, settings.CurrentMode);
        Assert.Same(item, page.HeroStatusItems[0]);
    }

    [Fact]
    public async Task FailedTransition_RequiresANewObservationBeforeHighlightingTheBaseline()
    {
        FakeMasterRuntime runtime = new() { Snapshot = ObservedNetwork(ClashSharpMode.Disabled) };
        FakeMasterTakeover takeover = new() { ExceptionToThrow = new InvalidOperationException("failed after admission") };
        MasterControlViewModel page = CreateViewModel(runtime: runtime, takeover: takeover);
        await page.LoadAsync(CancellationToken.None);

        await page.ApplyModeAsync(ClashSharpMode.RuleTakeover, CancellationToken.None);

        Assert.Equal(ClashSharpMode.Faulted, page.SelectedMode);
        Assert.Equal("Unconfirmed", page.BasicStatusText);
        await page.LoadAsync(CancellationToken.None);
        Assert.Equal(ClashSharpMode.Disabled, page.SelectedMode);
        Assert.Equal("Ready", page.BasicStatusText);
    }

    [Fact]
    public async Task VerifiedRunningCore_RemainsActiveWhenTheSeparateVersionProbeFails()
    {
        MasterControlViewModel page = CreateViewModel(
            core: new FakeMasterCore { ExceptionToThrow = new InvalidOperationException("version unavailable") },
            runtime: new FakeMasterRuntime { Snapshot = ObservedNetwork(ClashSharpMode.RuleTakeover) });

        await page.LoadAsync(CancellationToken.None);

        Assert.Equal("Active", page.BasicStatusText);
        Assert.Equal("Running", page.InfoTiles.Single(tile => tile.Id == "core").Value);
        Assert.True(page.IsRuleTakeoverModeSelected);
    }

    [Fact]
    public async Task VerifiedNetwork_DoesNotTurnUnavailableAggregateCountersIntoKnownValues()
    {
        MasterControlViewModel page = CreateViewModel(runtime: new FakeMasterRuntime
        {
            Snapshot = ObservedNetwork(ClashSharpMode.RuleTakeover) with { IsAvailable = false },
        });

        await page.LoadAsync(CancellationToken.None);

        Assert.Equal(ClashSharpMode.RuleTakeover, page.SelectedMode);
        Assert.Equal("Active", page.BasicStatusText);
        Assert.Equal("Running", page.InfoTiles.Single(tile => tile.Id == "core").Value);
        Assert.Equal("Unavailable", page.InfoTiles.Single(tile => tile.Id == "profile-count").Value);
        Assert.Equal("Unavailable", page.InfoTiles.Single(tile => tile.Id == "dns-mode").Value);
    }

    [Fact]
    public async Task EarlierObservation_CannotReplaceACompletedModeChange()
    {
        TaskCompletionSource<MasterControlRuntimeSnapshot> delayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMasterRuntime runtime = new() { SnapshotTask = delayed.Task };
        FakeMasterTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(ClashSharpMode.Standby, true, false, false, "verified"),
        };
        MasterControlViewModel page = CreateViewModel(runtime: runtime, takeover: takeover);
        Task refresh = page.LoadAsync(CancellationToken.None);
        await page.ApplyModeAsync(ClashSharpMode.Standby, CancellationToken.None);

        delayed.SetResult(ObservedNetwork(ClashSharpMode.Disabled));
        await refresh;

        Assert.Equal(ClashSharpMode.Standby, page.SelectedMode);
        Assert.Equal("Ready", page.BasicStatusText);
        Assert.Equal("Running", page.InfoTiles.Single(tile => tile.Id == "core").Value);
    }

    private static MasterControlRuntimeSnapshot ObservedNetwork(ClashSharpMode mode) => MasterControlRuntimeSnapshot.Unavailable with
    {
        IsAvailable = true,
        NetworkState = new NetworkStateSnapshot(mode, mode != ClashSharpMode.Disabled,
            mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover, false, 7890, "verified-test-state"),
    };

    [Fact]
    public async Task RuntimeUnknown_DoesNotSelectTheSavedTakeoverMode()
    {
        MasterControlViewModel page = CreateViewModel(
            settings: new FakeMasterSettings { CurrentMode = ClashSharpMode.RuleTakeover });

        await page.LoadAsync(CancellationToken.None);

        Assert.Equal(ClashSharpMode.Faulted, page.SelectedMode);
        Assert.False(page.IsRuleTakeoverModeSelected);
        Assert.False(page.IsDisabledModeSelected);
    }

    [Fact]
    public async Task RuntimeUnknown_CurrentModeTileDoesNotReportTheSavedChoice()
    {
        MasterControlViewModel page = CreateViewModel(
            settings: new FakeMasterSettings { CurrentMode = ClashSharpMode.FullTakeover });

        await page.LoadAsync(CancellationToken.None);
        await page.SetHeroStatusSlotAsync(0, MasterHeroStatusItemKind.CurrentMode, CancellationToken.None);

        Assert.Equal("Unconfirmed", page.InfoTiles.Single(tile => tile.Id == "current-mode").Value);
        Assert.Equal("Unconfirmed", page.HeroStatusItems[0].Value);
    }

    [Fact]
    public async Task Availability_UsesRuntimeStatusRatherThanOnlyAnInstalledExecutable()
    {
        MasterControlViewModel page = CreateViewModel(
            settings: new FakeMasterSettings { CurrentMode = ClashSharpMode.RuleTakeover });

        await page.LoadAsync(CancellationToken.None);

        Assert.Equal("Unconfirmed", page.BasicStatusText);
        Assert.Equal(page.BasicStatusText,
            page.HeroStatusItems.Single(item => item.Kind == MasterHeroStatusItemKind.Availability).Value);
    }
}
