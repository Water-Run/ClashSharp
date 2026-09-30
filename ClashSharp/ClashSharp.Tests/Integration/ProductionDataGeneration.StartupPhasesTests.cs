extern alias ClashSharpUi;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using ClashSharp.Settings;
using UiData = ClashSharpUi::ClashSharp.Hosting.Data;
using UiModel = ClashSharpUi::ClashSharp.Model;
using UiService = ClashSharpUi::ClashSharp.Service;
using UiStartup = ClashSharpUi::ClashSharp.Hosting.Startup;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Theory]
    [InlineData(StartupBehaviorMode.LastSetting, ClashSharpMode.RuleTakeover, ClashSharpMode.RuleTakeover)]
    [InlineData(StartupBehaviorMode.DisableProxy, ClashSharpMode.RuleTakeover, ClashSharpMode.Disabled)]
    [InlineData(StartupBehaviorMode.StartRuleProxy, ClashSharpMode.Disabled, ClashSharpMode.RuleTakeover)]
    public async Task StartupPhases_NetworkWaitsForConflictCaptureAndUsesTheConfiguredPolicy(
        StartupBehaviorMode policy, ClashSharpMode savedMode, ClashSharpMode expectedMode)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.CurrentMode.Value] = (int)savedMode;
        fixture.LegacyValues[SettingsRegistry.Keys.StartupBehaviorMode.Value] = (int)policy;
        fixture.LegacyValues[SettingsRegistry.Keys.TransparentProxyEnabled.Value] = false;
        NetworkSurface network = new();
        List<ClashSharpMode> applied = [];
        network.BeforeApply = (configuration, _) => applied.Add(configuration.Mode);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings);

        Assert.Equal(StartupStepOutcome.Succeeded, (await data.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);

        Assert.Empty(applied);
        var runtime = RuntimeOf(fixture);
        Assert.False(runtime.IsExecutionPublished);
        Assert.False(runtime.Sampling.IsRunning);
        Assert.Contains(runtime.Repositories.Session.Snapshot.PendingApplications, batch => batch.ApplicationKind == SettingApplicationKind.Network);
        Assert.Equal(savedMode, settings.CurrentMode);
        UiStartup.StartupConflictSnapshot conflicts = new();
        conflicts.Capture([]);
        List<ClashSharpMode> notifications = [];
        var activation = new UiStartup.GenerationRuntimeActivationStartupStep(data, conflicts, (mode, token) =>
        {
            Assert.False(token.CanBeCanceled);
            Assert.False(runtime.IsExecutionPublished);
            Assert.NotEqual(MutationAdmissionState.Open, fixture.Admission.State);
            notifications.Add(mode);
            return Task.CompletedTask;
        });
        Assert.True(data.Order < 425 && activation.Order > 425);

        Assert.Equal(StartupStepOutcome.Succeeded, (await activation.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);

        Assert.Equal(expectedMode, settings.CurrentMode);
        Assert.Equal(expectedMode, (await network.ReadConfigurationAsync(CancellationToken.None)).Mode);
        Assert.DoesNotContain(applied, mode => mode != expectedMode);
        Assert.Equal([expectedMode], notifications);
        Assert.True(runtime.IsExecutionPublished);
        Assert.True(runtime.Sampling.IsRunning);
        Assert.Empty(runtime.Repositories.Session.Snapshot.PendingApplications);
        _ = await activation.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        Assert.Single(notifications);
    }

    [Theory]
    [InlineData("missing", false, StartupBehaviorMode.LastSetting, false)]
    [InlineData("failed", false, StartupBehaviorMode.LastSetting, false)]
    [InlineData("port", false, StartupBehaviorMode.LastSetting, false)]
    [InlineData("tun", true, StartupBehaviorMode.LastSetting, false)]
    [InlineData("tun", false, StartupBehaviorMode.LastSetting, true)]
    [InlineData("port", false, StartupBehaviorMode.DisableProxy, true)]
    public async Task StartupPhases_ConflictDecisionCannotBeBypassedByLaterRuntimeReconciliation(
        string conflict, bool tun, StartupBehaviorMode policy, bool allowed)
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.CurrentMode.Value] = (int)ClashSharpMode.RuleTakeover;
        fixture.LegacyValues[SettingsRegistry.Keys.StartupBehaviorMode.Value] = (int)policy;
        fixture.LegacyValues[SettingsRegistry.Keys.TransparentProxyEnabled.Value] = tun;
        NetworkSurface network = new();
        List<ClashSharpMode> applied = [];
        network.BeforeApply = (configuration, _) => applied.Add(configuration.Mode);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), network);
        UiService.AppSettingsService settings = new(new Dictionary<string, object>());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), settings);
        _ = await data.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        UiStartup.StartupConflictSnapshot conflicts = new();
        if (conflict == "failed") { conflicts.CaptureFailure(); }
        if (conflict is "port" or "tun")
        {
            conflicts.Capture([new UiModel.StartupConflictIssue(conflict == "port"
                ? UiModel.StartupConflictKind.MixedPortOccupied : UiModel.StartupConflictKind.ActiveTunInterface, "conflict", "details")]);
        }
        List<ClashSharpMode> notifications = [];
        var activation = new UiStartup.GenerationRuntimeActivationStartupStep(data, conflicts,
            (mode, _) => { notifications.Add(mode); return Task.CompletedTask; });

        var result = await activation.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(allowed ? StartupStepOutcome.Succeeded : StartupStepOutcome.Warning, result.Outcome);
        if (!allowed)
        {
            Assert.Empty(applied);
            Assert.Empty(notifications);
            Assert.Equal(ClashSharpMode.RuleTakeover, settings.CurrentMode);
            Assert.Equal(ClashSharpMode.Disabled, (await network.ReadConfigurationAsync(CancellationToken.None)).Mode);
            Assert.Equal(SettingAppliedStateKind.Unknown, RuntimeOf(fixture).Repositories.Session.Snapshot.Applied[SettingsRegistry.Keys.CurrentMode].Kind);
        }
        else
        {
            ClashSharpMode expected = policy == StartupBehaviorMode.DisableProxy ? ClashSharpMode.Disabled : ClashSharpMode.RuleTakeover;
            Assert.Equal(expected, settings.CurrentMode);
            Assert.Equal(expected, (await network.ReadConfigurationAsync(CancellationToken.None)).Mode);
            Assert.Equal([expected], notifications);
        }
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
    }

    [Fact]
    public async Task StartupPhases_ActivationCannotOpenDataOrRunWithoutThePreparationStep()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), new(new Dictionary<string, object>()));
        UiStartup.StartupConflictSnapshot conflicts = new();
        conflicts.Capture([]);
        var activation = new UiStartup.GenerationRuntimeActivationStartupStep(data, conflicts,
            (_, _) => throw new InvalidOperationException("No runtime has been opened."));

        Assert.Equal(StartupStepOutcome.Fatal, (await activation.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        Assert.Empty(fixture.Containers);
        Assert.False(Directory.Exists(directory.RootPath));
    }

    [Fact]
    public async Task StartupPhases_SettingsObserverFailureDoesNotSuppressTheVerifiedProxyStartedEvent()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        fixture.LegacyValues[SettingsRegistry.Keys.CurrentMode.Value] = (int)ClashSharpMode.RuleTakeover;
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), new(new Dictionary<string, object>()));
        _ = await data.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        fixture.Authority.StateChanged += _ => throw new IOException("settings subscriber unavailable");
        UiStartup.StartupConflictSnapshot conflicts = new();
        conflicts.Capture([]);
        List<ClashSharpMode> published = [];
        var activation = new UiStartup.GenerationRuntimeActivationStartupStep(data, conflicts,
            (mode, _) => { published.Add(mode); return Task.CompletedTask; });

        Assert.Equal(StartupStepOutcome.Warning, (await activation.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        Assert.Equal([ClashSharpMode.RuleTakeover], published);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task StartupPhases_NotificationFailureKeepsVerifiedActivationAndReleasesHeldExecution()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        var data = fixture.CreateDataOpenStep(new RuntimeLifetimeRegistry(), new UiData.GenerationSamplingRuntime(fixture.Manager), new(new Dictionary<string, object>()));
        _ = await data.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);
        UiStartup.StartupConflictSnapshot conflicts = new();
        conflicts.Capture([]);
        var activation = new UiStartup.GenerationRuntimeActivationStartupStep(data, conflicts,
            (_, _) => throw new IOException("notification unavailable"));

        Assert.Equal(StartupStepOutcome.Warning, (await activation.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        Assert.True(RuntimeOf(fixture).IsExecutionPublished);
        Assert.Empty(RuntimeOf(fixture).Repositories.Session.Snapshot.PendingApplications);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }
}
