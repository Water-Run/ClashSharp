using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerLaunchPresentationTests
{
    [Fact]
    public async Task CompletedInstallationOffersExplicitLaunchAndExplainsFutureManagement()
    {
        Runtime runtime = new();
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        Assert.False(model.IsLaunchApplicationVisible);

        await model.PrimaryActionCommand.ExecuteAsync();

        Assert.True(model.IsLaunchApplicationVisible);
        Assert.Contains("Setup.exe", model.StatusDetail, StringComparison.Ordinal);
        Assert.Contains("启动 Clash#", model.StatusDetail, StringComparison.Ordinal);
        Assert.Equal(0, runtime.Launches);
        await model.LaunchApplicationCommand.ExecuteAsync();
        Assert.Equal(1, runtime.Launches);
        Assert.Equal("installer.launch.completed", model.DiagnosticCode);
    }

    [Fact]
    public async Task UninstallNeverOffersLaunch()
    {
        Runtime runtime = new() { Installed = true };
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        Assert.True(model.IsLaunchApplicationVisible);

        await model.SecondaryActionCommand.ExecuteAsync();

        Assert.False(model.IsLaunchApplicationVisible);
        Assert.Equal(0, runtime.Launches);
    }

    [Fact]
    public async Task ActivationFailurePreservesInstalledStateAndAllowsRetry()
    {
        Runtime runtime = new() { FailLaunch = true };
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        await model.PrimaryActionCommand.ExecuteAsync();

        await model.LaunchApplicationCommand.ExecuteAsync();

        Assert.True(model.IsLaunchApplicationVisible);
        Assert.Contains("已安装", model.StatusTitle, StringComparison.Ordinal);
        Assert.DoesNotContain("private", model.StatusDetail, StringComparison.Ordinal);
        runtime.FailLaunch = false;
        await model.LaunchApplicationCommand.ExecuteAsync();
        Assert.Equal(2, runtime.Launches);
    }

    private sealed class Runtime : IInstallerRuntime, IInstallerApplicationLaunchRuntime
    {
        internal bool Installed { get; init; }
        internal bool FailLaunch { get; set; }
        internal int Launches { get; private set; }
        public bool SupportsApplicationLaunch => true;
        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) =>
            Task.FromResult(InstallerPresentationTestData.Readiness(Installed ? InstallerProductState.Installed : InstallerProductState.Available));
        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation, IProgress<InstallerProgress> progress, CancellationToken cancellationToken) =>
            Task.FromResult(InstallerPresentationTestData.Result());
        public Task LaunchApplicationAsync(CancellationToken cancellationToken)
        {
            Launches++;
            return FailLaunch ? Task.FromException(new IOException("private path")) : Task.CompletedTask;
        }
    }
}
