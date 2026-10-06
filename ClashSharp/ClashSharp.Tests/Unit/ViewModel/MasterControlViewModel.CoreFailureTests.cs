using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover, false, MihomoCoreOwner.None)]
    [InlineData(ClashSharpMode.FullTakeover, true, MihomoCoreOwner.None)]
    [InlineData(ClashSharpMode.Standby, false, MihomoCoreOwner.App)]
    public async Task OperatingModeWithoutVerifiedCore_DoesNotAdvertiseAnActiveRuntime(
        ClashSharpMode mode, bool ownershipKnown, MihomoCoreOwner owner)
    {
        FakeMasterRuntime runtime = new()
        {
            Snapshot = MasterControlRuntimeSnapshot.Unavailable with
            {
                IsAvailable = true,
                RuntimeOwnershipKnown = ownershipKnown,
                EffectiveOwner = owner,
            },
        };
        MasterControlViewModel viewModel = CreateViewModel(
            settings: new FakeMasterSettings { CurrentMode = mode }, runtime: runtime);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Unavailable", viewModel.BasicStatusText);
    }

    [Fact]
    public async Task CoreLossAfterSuccessfulObservation_UpdatesTheOverallStatus()
    {
        FakeMasterRuntime runtime = new()
        {
            Snapshot = MasterControlRuntimeSnapshot.Unavailable with
            {
                IsAvailable = true,
                RuntimeOwnershipKnown = true,
                EffectiveOwner = MihomoCoreOwner.App,
            },
        };
        MasterControlViewModel viewModel = CreateViewModel(
            settings: new FakeMasterSettings { CurrentMode = ClashSharpMode.RuleTakeover }, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Active", viewModel.BasicStatusText);
        List<string?> notifications = [];
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);
        runtime.Snapshot = MasterControlRuntimeSnapshot.Unavailable;
        viewModel.InvalidateAfterAction();

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Unavailable", viewModel.BasicStatusText);
        Assert.Contains(nameof(MasterControlViewModel.BasicStatusText), notifications);
    }

    [Theory]
    [InlineData(ClashSharpMode.RuleTakeover, "Active")]
    [InlineData(ClashSharpMode.FullTakeover, "Active")]
    [InlineData(ClashSharpMode.Standby, "Ready")]
    public async Task ReselectingAnOperatingMode_ReobservesRuntimeAndCanRecoverCoreFailure(
        ClashSharpMode mode, string expectedStatus)
    {
        FakeMasterSettings settings = new() { CurrentMode = mode };
        FakeMasterTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(mode, true, mode != ClashSharpMode.Standby, false, "recovered"),
        };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, takeover: takeover);
        await viewModel.LoadAsync(CancellationToken.None);

        await viewModel.ApplyModeAsync(mode, CancellationToken.None);

        Assert.Equal(1, takeover.ApplyCount);
        Assert.Equal(mode, viewModel.SelectedMode);
        Assert.Equal("Running", viewModel.CoreStatusText);
        Assert.Equal(expectedStatus, viewModel.BasicStatusText);
    }

    [Fact]
    public async Task DataReplacementInvalidatesThePreviouslyObservedCoreStatus()
    {
        FakeMasterRuntime runtime = new()
        {
            Snapshot = MasterControlRuntimeSnapshot.Unavailable with
            {
                IsAvailable = true,
                RuntimeOwnershipKnown = true,
                EffectiveOwner = MihomoCoreOwner.Service,
            },
        };
        MasterControlViewModel viewModel = CreateViewModel(
            settings: new FakeMasterSettings { CurrentMode = ClashSharpMode.FullTakeover }, runtime: runtime);
        await viewModel.LoadAsync(CancellationToken.None);
        Assert.Equal("Active", viewModel.BasicStatusText);
        List<string?> notifications = [];
        viewModel.PropertyChanged += (_, args) => notifications.Add(args.PropertyName);

        viewModel.InvalidateForDataChange();

        Assert.Equal("Unavailable", viewModel.BasicStatusText);
        Assert.Contains(nameof(MasterControlViewModel.BasicStatusText), notifications);
    }
}
