using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed partial class ProductionInstallerRuntimeTests
{
    [Fact]
    public async Task UpgradePresentationRecheckReturnsToRepairAfterTheVersionMatches()
    {
        using var olderRuntime = new ProductionInstallerRuntime(new RecordingBackend(Inspection(true, "1.2.3.3", false)));
        using var matchingRuntime = new ProductionInstallerRuntime(new RecordingBackend(Inspection(true, Version, false)));
        InstallerRuntimeReadiness older = await olderRuntime.InspectReadinessAsync(CancellationToken.None);
        InstallerRuntimeReadiness matching = await matchingRuntime.InspectReadinessAsync(CancellationToken.None);
        var scripted = new ScriptedInstallerRuntime { Inspect = _ => Task.FromResult(older) };
        using var viewModel = new InstallerShellViewModel(scripted);
        await viewModel.InitializeAsync();
        Assert.Equal("更新", viewModel.PrimaryActionText);
        Assert.Equal("可更新", viewModel.StatusBadge);

        scripted.Inspect = _ => Task.FromResult(matching);
        await viewModel.RefreshCommand.ExecuteAsync();

        Assert.Equal("修复", viewModel.PrimaryActionText);
        Assert.Equal("已安装", viewModel.StatusBadge);
        Assert.Empty(scripted.Operations);
    }

    [Theory]
    [InlineData("not-installed")]
    [InlineData("blocked")]
    [InlineData("recovery")]
    [InlineData("uninstall-only")]
    public async Task UpgradePresentationCannotAuthorizeAnOtherwiseUnavailableMaintenanceAction(string state)
    {
        InstallerRuntimeReadiness readiness = state switch
        {
            "not-installed" => InstallerPresentationTestData.Readiness(),
            "blocked" => InstallerPresentationTestData.Readiness(InstallerProductState.Installed, canExecute: false),
            "recovery" => InstallerPresentationTestData.Readiness(InstallerProductState.RecoveryRequired, InstallerOperation.Repair),
            _ => InstallerPresentationTestData.Readiness(InstallerProductState.Installed, allowedOperations: [InstallerOperation.Uninstall]),
        };
        var runtime = new ScriptedInstallerRuntime { Inspect = _ => Task.FromResult(readiness with { IsUpgrade = true }) };
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        Assert.Equal("installer.runtime.readiness_invalid", viewModel.DiagnosticCode);
        Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
        Assert.Empty(runtime.Operations);
    }

    [Theory]
    [InlineData("1.2.3.3", "更新", "可以更新")]
    [InlineData("1.2.3.4", "修复", "已安装")]
    public async Task UpgradePresentationUsesNumericPackageVersionsForTheMaintenanceLabel(string installed, string action, string title)
    {
        var backend = new RecordingBackend(Inspection(true, installed, false));
        using var runtime = new ProductionInstallerRuntime(backend);
        using var viewModel = new InstallerShellViewModel(runtime);

        await viewModel.InitializeAsync();

        Assert.Equal(action, viewModel.PrimaryActionText);
        Assert.Equal(title, viewModel.StatusTitle);
        Assert.Equal("卸载", viewModel.SecondaryActionText);
        Assert.Empty(backend.Operations);
        if (action == "更新")
        {
            Assert.Contains(installed, viewModel.StatusDetail, StringComparison.Ordinal);
            Assert.Contains(Version, viewModel.StatusDetail, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task UpgradePresentationProgressSaysUpdateAndUsesTheExistingVerifiedMaintenanceOperation()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var backend = new RecordingBackend(Inspection(true, "1.2.3.3", false))
        {
            BeforeExecute = _ =>
            {
                entered.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10), CancellationToken.None));
            },
            ExecuteResult = InstallerPresentationTestData.Result(),
        };
        using var runtime = new ProductionInstallerRuntime(backend);
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();
        Task execution = viewModel.PrimaryActionCommand.ExecuteAsync();
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal("正在更新", viewModel.StatusTitle);
            Assert.False(viewModel.PrimaryActionCommand.CanExecute(null));
        }
        finally
        {
            release.Set();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal([InstallerOperation.Repair], backend.Operations);
    }

    [Theory]
    [InlineData("1.2.3.10")]
    [InlineData("1.3.0.0")]
    public async Task NewerInstalledVersionOffersOnlyRemovalAndExplainsTheDowngradeBlock(string installed)
    {
        var backend = new RecordingBackend(Inspection(true, installed, false));
        using var runtime = new ProductionInstallerRuntime(backend);

        InstallerRuntimeReadiness readiness = await runtime.InspectReadinessAsync(CancellationToken.None);
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        Assert.Equal([InstallerOperation.Uninstall], readiness.AllowedOperations);
        Assert.False(readiness.IsUpgrade);
        Assert.All(readiness.Capabilities, static capability => Assert.True(capability.IsReady));
        Assert.Equal("已安装更新的版本", viewModel.StatusTitle);
        Assert.Contains(installed, viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Contains(Version, viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("不会降级", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("卸载", viewModel.PrimaryActionText);
        Assert.False(viewModel.HasSecondaryAction);
        Assert.Empty(backend.Operations);
    }

    [Fact]
    public async Task DowngradeRejectedAtExecutionExplainsThatNothingChanged()
    {
        var runtime = new ScriptedInstallerRuntime
        {
            Execute = (_, _, _) => Task.FromResult(new InstallerExecutionResult(
                InstallerExecutionOutcome.Blocked, "installer.package.downgrade_rejected", null, RecoveryPending: false)),
        };
        using var viewModel = new InstallerShellViewModel(runtime);
        await viewModel.InitializeAsync();

        await viewModel.PrimaryActionCommand.ExecuteAsync();

        Assert.Equal("已安装更新的版本", viewModel.StatusTitle);
        Assert.Contains("系统未被更改", viewModel.StatusDetail, StringComparison.Ordinal);
        Assert.Equal("installer.package.downgrade_rejected", viewModel.DiagnosticCode);
        Assert.Equal("操作未开始。", viewModel.ProgressStatus);
    }
}
