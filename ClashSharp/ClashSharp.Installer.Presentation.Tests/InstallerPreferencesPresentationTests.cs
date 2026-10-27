using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Presentation;
using ClashSharp.Installer.Runtime;

namespace ClashSharp.Installer.Presentation.Tests;

public sealed class InstallerPreferencesPresentationTests
{
    [Fact]
    public async Task FreshInstallOffersRecommendedChoicesAndSendsOneImmutableSnapshot()
    {
        var runtime = new Runtime();
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        Assert.True(model.IsInstallPreferencesVisible);
        Assert.True(model.CreateDesktopShortcut);
        Assert.False(model.RequestTaskbarPin);
        Assert.False(model.LaunchAtStartup);
        Assert.True(model.RegisterProxyRecovery);
        Assert.True(model.InstallTransparentProxyService);
        Assert.True(model.FollowSystemDefaults);
        Assert.False(model.EnableMcpServer);
        model.CreateDesktopShortcut = false;
        model.EnableMcpServer = true;
        Task active = model.PrimaryActionCommand.ExecuteAsync();
        try
        {
            InstallerPreferences snapshot = await runtime.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(snapshot.CreateDesktopShortcut);
            Assert.True(snapshot.EnableMcpServer);
            model.CreateDesktopShortcut = true;
            Assert.False(model.CreateDesktopShortcut);
            Assert.Same(snapshot, runtime.Snapshot);
            Assert.Equal(0, runtime.LegacyCalls);
        }
        finally
        {
            runtime.Release.TrySetResult();
            await active.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task TaskbarSelectionPreservesOtherDrafts()
    {
        var runtime = new Runtime();
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        model.LaunchAtStartup = true;
        model.RequestTaskbarPin = true;
        await model.RefreshCommand.ExecuteAsync();
        Assert.True(model.RequestTaskbarPin);
        Assert.True(model.LaunchAtStartup);
        model.RequestTaskbarPin = false;
        Assert.False(model.RequestTaskbarPin);
    }

    [Fact]
    public async Task PendingUserSetupUsesItsOwnRetryWithoutCallingInstallOrRepair()
    {
        var runtime = new Runtime { Maintenance = true, Pending = true };
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        Assert.True(model.IsCompletePreferencesVisible);
        Assert.False(model.IsInstallPreferencesVisible);
        await model.CompletePreferencesCommand.ExecuteAsync();
        Assert.Equal(1, runtime.CompletionCalls);
        Assert.Equal(0, runtime.LegacyCalls);
        Assert.False(runtime.Accepted.Task.IsCompleted);
        Assert.False(model.IsCompletePreferencesVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnsupportedCompositionOrMaintenanceDoesNotOfferOrApplyNewDefaults(bool maintenance)
    {
        var runtime = new Runtime { Supported = maintenance, Maintenance = maintenance };
        using var model = new InstallerShellViewModel(runtime);
        await model.InitializeAsync();
        Assert.False(model.IsInstallPreferencesVisible);
        model.EnableMcpServer = true;
        Assert.False(model.EnableMcpServer);
        await model.PrimaryActionCommand.ExecuteAsync();
        Assert.Equal(1, runtime.LegacyCalls);
        Assert.False(runtime.Accepted.Task.IsCompleted);
    }

    private sealed class Runtime : IInstallerRuntime, IInstallerPreferencesRuntime
    {
        internal bool Supported { get; init; } = true;
        internal bool Maintenance { get; init; }
        internal bool Pending { get; init; }
        internal int CompletionCalls { get; private set; }
        internal int LegacyCalls { get; private set; }
        internal InstallerPreferences? Snapshot { get; private set; }
        internal TaskCompletionSource<InstallerPreferences> Accepted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool SupportsInstallPreferences => Supported;
        public Task<InstallerRuntimeReadiness> InspectReadinessAsync(CancellationToken cancellationToken) => Task.FromResult(
            new InstallerRuntimeReadiness(true, "installer.runtime.ready", "就绪", "检查完成。", "1.0.0",
                Maintenance ? InstallerProductState.Installed : InstallerProductState.Available, null,
                Maintenance ? [InstallerOperation.Repair, InstallerOperation.Uninstall] : [InstallerOperation.Install],
                [new("系统", "就绪", true)])
            { PendingUserPreferences = Pending });
        public Task<InstallerExecutionResult> ExecuteAsync(InstallerOperation operation, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            LegacyCalls++;
            return Task.FromResult(new InstallerExecutionResult(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false));
        }
        public async Task<InstallerExecutionResult> InstallWithPreferencesAsync(InstallerPreferences preferences, IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            Snapshot = preferences;
            Accepted.TrySetResult(preferences);
            await Release.Task.WaitAsync(cancellationToken);
            return new(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false);
        }
        public Task<InstallerExecutionResult> CompletePreferencesAsync(IProgress<InstallerProgress> progress, CancellationToken cancellationToken)
        {
            CompletionCalls++;
            return Task.FromResult(new InstallerExecutionResult(InstallerExecutionOutcome.Succeeded, "installer.completed", InstallerTransactionPhase.Verified, false));
        }
    }
}
