extern alias ClashSharpUi;
using System.Runtime.CompilerServices;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Model;
using ClashSharp.Settings;
using Composer = ClashSharpUi::ClashSharp.Hosting.Data.AppDataGenerationRuntimeComposer;
using Connections = ClashSharpUi::ClashSharp.Service.MihomoConnectionService;
using EventHub = ClashSharpUi::ClashSharp.Service.TriggerRuntimeEventHub;
using NetworkConfiguration = ClashSharpUi::ClashSharp.Hosting.Settings.NetworkSettingsConfiguration;
using NetworkRuntime = ClashSharpUi::ClashSharp.Hosting.Settings.INetworkSettingsRuntime;
using Notifications = ClashSharpUi::ClashSharp.Service.NotificationService;
using Proxy = ClashSharpUi::ClashSharp.Service.WindowsProxyService;
using RuntimeServices = ClashSharpUi::ClashSharp.Hosting.Data.AppDataGenerationRuntime;
using SamplingFacade = ClashSharpUi::ClashSharp.Hosting.Data.GenerationSamplingRuntime;
using ServiceManager = ClashSharpUi::ClashSharp.Service.MihomoServiceManager;
using SettingsService = ClashSharpUi::ClashSharp.Service.AppSettingsService;
using StartupLog = ClashSharpUi::ClashSharp.Service.IStartupLaunchLog;
using StartupService = ClashSharpUi::ClashSharp.Service.StartupLaunchService;
using StartupStep = ClashSharpUi::ClashSharp.Hosting.Startup.DataGenerationStartupStep;
using StartupTask = ClashSharpUi::ClashSharp.Service.IStartupLaunchTask;
using StartupTaskProvider = ClashSharpUi::ClashSharp.Service.IStartupLaunchTaskProvider;
using StartupTaskState = ClashSharpUi::ClashSharp.Service.StartupLaunchTaskState;
using Takeover = ClashSharpUi::ClashSharp.Service.NetworkTakeoverService;
using Traffic = ClashSharpUi::ClashSharp.Service.RuntimeTrafficRateService;

namespace ClashSharp.Tests.Integration;

public sealed partial class ProductionDataGenerationTests
{
    [Fact]
    public async Task RealStartupComposition_OwnsEveryParticipantAndProducerBeforeApplyingSettings()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        RuntimeLifetimeRegistry lifetime = new();
        StartupPlatform platform = new();
        AppearanceSurface appearance = new();
        NetworkSurface network = new();
        ConfigureRealRuntime(fixture, platform, appearance, network);
        Dictionary<string, object> legacy = new() { ["MixedPort"] = 54321 };
        SettingsService settings = new(legacy);
        SamplingFacade sampling = new(fixture.Manager);
        StartupStep step = fixture.CreateStartupStep(lifetime, sampling, settings);
        Assert.False(Directory.Exists(directory.RootPath));

        StartupStepResult result = await step.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        var repositories = Assert.Single(fixture.Containers);
        RuntimeServices runtime = Assert.IsType<RuntimeServices>(repositories.GetService(typeof(RuntimeServices)));
        Assert.Empty(repositories.Session.Snapshot.PendingApplications);
        Assert.All(repositories.Session.Snapshot.Applied.Values, state => Assert.Equal(SettingAppliedStateKind.Verified, state.Kind));
        Assert.True(runtime.Sampling.IsRunning);
        Assert.True(runtime.TriggerSettings.Scheduler.IsRunning);
        Assert.True(runtime.TriggerSettings.Scheduler.IsAcceptingEvents);
        Assert.Equal(10000, settings.MixedPort);
        Assert.Equal(54321, legacy["MixedPort"]);
        Assert.Equal(StartupStepOutcome.Succeeded,
            (await step.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        RuntimeShutdownResult shutdown = await new RuntimeLifecycleCoordinator(fixture.Admission, lifetime)
            .ShutdownAsync(CancellationToken.None);
        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, shutdown.Outcome);
        Assert.False(runtime.Sampling.IsRunning);
        Assert.False(runtime.TriggerSettings.Scheduler.IsRunning);
        await fixture.Manager.DisposeAsync();
        Assert.False(runtime.Sampling.IsRunning);
        Assert.False(runtime.TriggerSettings.Scheduler.IsRunning);
        Assert.Throws<DataGenerationManagerException>(() => settings.MixedPort);
    }

    [Fact]
    public async Task BoundSettings_ReadAndNotifyFromAuthorityAndRejectSynchronousLegacyWrites()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        Dictionary<string, object> legacy = new() { ["NotificationEnabled"] = true, ["MixedPort"] = 54321 };
        SettingsService settings = new(legacy);
        StartupStep step = fixture.CreateStartupStep(new RuntimeLifetimeRegistry(), new SamplingFacade(fixture.Manager), settings);
        Assert.Equal(StartupStepOutcome.Succeeded, (await step.ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        List<string> changes = [];
        settings.SettingChanged += (_, change) => changes.Add(change.Key);
        SettingDefinition enabled = SettingsRegistry.Default.Get(SettingsRegistry.Keys.NotificationEnabled.Value);

        await settings.ApplyChangesAsync([new(enabled.Key, enabled.Normalize("false").Value!)], CancellationToken.None);

        Assert.False(settings.NotificationEnabled);
        Assert.Equal(["NotificationEnabled"], changes);
        Assert.Equal(true, legacy["NotificationEnabled"]);
        Assert.Throws<InvalidOperationException>(() => settings.NotificationEnabled = true);
        using (MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary())
        {
            Assert.Throws<InvalidOperationException>(() => settings.WriteAdmitted(lease, editor => editor.MixedPort = 7890));
        }
        Assert.Equal(10000, settings.MixedPort);
        Assert.Equal(54321, legacy["MixedPort"]);
        await settings.ResetPreferenceGroupAsync(SettingsResetScope.Notifications, CancellationToken.None);
        Assert.True(settings.NotificationEnabled);
    }

    [Fact]
    public async Task StartupNativeFailure_LeavesPendingEvidenceAndStillOwnsAllCreatedResources()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface { Unknown = true });
        SettingsService settings = new(new Dictionary<string, object>());
        RuntimeLifetimeRegistry lifetime = new();

        StartupStepResult result = await fixture.CreateStartupStep(lifetime, new SamplingFacade(fixture.Manager), settings)
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Fatal, result.Outcome);
        var repositories = Assert.Single(fixture.Containers);
        RuntimeServices runtime = Assert.IsType<RuntimeServices>(repositories.GetService(typeof(RuntimeServices)));
        Assert.Contains(repositories.Session.Snapshot.PendingApplications,
            batch => batch.ApplicationKind == SettingApplicationKind.Network && batch.State == SettingsApplicationBatchState.Failed);
        Assert.False(runtime.Sampling.IsRunning);
        Assert.False(runtime.TriggerSettings.Scheduler.IsRunning);
        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal,
            (await new RuntimeLifecycleCoordinator(fixture.Admission, lifetime).ShutdownAsync(CancellationToken.None)).Outcome);
        await fixture.Manager.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => repositories.Session.Snapshot);
    }

    [Fact]
    public async Task GenerationLifecycleFacade_RejectsAResumeFromTheRetiredGeneration()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        SamplingFacade sampling = new(fixture.Manager);
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(
            new RuntimeLifetimeRegistry(), sampling, new SettingsService(new Dictionary<string, object>()))
            .ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        QuiescedState oldState = await sampling.QuiesceAsync(CancellationToken.None);
        Assert.True(oldState.WasRunning);
        await fixture.ReplaceWithEmptyGenerationAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => sampling.ResumeAsync(oldState, CancellationToken.None));

        RuntimeServices current = Assert.IsType<RuntimeServices>(fixture.Containers[^1].GetService(typeof(RuntimeServices)));
        Assert.False(current.Sampling.IsRunning);
    }

    private static void ConfigureRealRuntime(Fixture fixture, StartupPlatform platform, AppearanceSurface appearance, NetworkSurface network)
    {
        _ = ConfigureSelections(fixture);
        StartupService startup = new(platform, platform, key => key);
        Composer composer = new(fixture.Admission, fixture.Authority,
            () => new OwnedUiDispatcher(() => true, action => { action(); return true; }, CancellationToken.None),
            appearance, startup, UnusedNative<Connections>(), UnusedNative<Traffic>(), CreateUnusedTakeover(),
            UnusedNative<Proxy>(), UnusedNative<ServiceManager>(), UnusedNative<Notifications>(), new EventHub(),
            new ApplicationLifetimeRequestChannel(), () => false, TimeProvider.System, Guid.NewGuid(), key => key,
            _ => network, new SuspendedClock());
        fixture.ComposeRuntime = composer.ComposeAsync;
    }

    [Fact]
    public async Task AuthorityPublication_ObserverFailureDoesNotSkipTheBoundReaderOrUndoTheCommit()
    {
        await using DataGenerationTestDirectory directory = new();
        await using Fixture fixture = new(directory);
        ConfigureRealRuntime(fixture, new StartupPlatform(), new AppearanceSurface(), new NetworkSurface());
        IOException failedObserver = new("observer unavailable");
        fixture.Authority.StateChanged += _ => throw failedObserver;
        SettingsService settings = new(new Dictionary<string, object>());
        Assert.Equal(StartupStepOutcome.Succeeded, (await fixture.CreateStartupStep(new RuntimeLifetimeRegistry(),
            new SamplingFacade(fixture.Manager), settings).ExecuteAsync(new AppLaunchRequest(""), CancellationToken.None)).Outcome);
        List<string> observed = [];
        settings.SettingChanged += (_, change) => observed.Add(change.Key);
        SettingDefinition definition = SettingsRegistry.Default.Get(SettingsRegistry.Keys.NotificationEnabled.Value);

        IOException error = await Assert.ThrowsAsync<IOException>(() => fixture.Authority.ApplyChangesAsync(
            [new(definition.Key, definition.Normalize("false").Value!)], Guid.NewGuid(), CancellationToken.None));

        Assert.Same(failedObserver, error);
        Assert.False(settings.NotificationEnabled);
        Assert.Equal(["NotificationEnabled"], observed);
        Assert.Empty(fixture.Containers[0].Session.Snapshot.PendingApplications);
    }

    private static T UnusedNative<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private sealed partial class Fixture
    {
        public StartupStep CreateStartupStep(RuntimeLifetimeRegistry lifetime, SamplingFacade sampling, SettingsService settings) =>
            new(_bootstrap, Manager, Admission, lifetime, sampling, Authority, settings);
    }

    private sealed class StartupPlatform : StartupTaskProvider, StartupTask, StartupLog
    {
        public StartupTaskState State { get; private set; } = StartupTaskState.Disabled;
        public Task<StartupTask> GetAsync(string taskId) => Task.FromResult<StartupTask>(this);
        public Task<StartupTaskState> RequestEnableAsync() { State = StartupTaskState.Enabled; return Task.FromResult(State); }
        public void Disable() => State = StartupTaskState.Disabled;
        public void AppendLog(string level, string category, string message, string? detail) { }
    }

    private sealed class AppearanceSurface : IAppearanceNativeSettings
    {
        private AppLanguage _language = AppLanguage.AutoDetect;
        private AppThemeMode _theme = AppThemeMode.FollowSystem;
        private AccentColorConfiguration _accent = new(AppAccentColorMode.FollowSystem, "#FF0078D4");
        public AppearanceNativeConfiguration CaptureConfiguration() => new(_language, _theme, _accent);
        public void ApplyLanguage(AppLanguage language) => _language = language;
        public void ApplyTheme(AppThemeMode theme) => _theme = theme;
        public void ApplyAccent(AccentColorConfiguration accent) => _accent = accent;
    }

    private sealed class NetworkSurface : NetworkRuntime
    {
        private NetworkConfiguration _installed = new(ClashSharpMode.Disabled, "builtin-direct", false, 10000);
        public bool Unknown { get; init; }
        public Task<NetworkConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken) => Unknown
            ? Task.FromException<NetworkConfiguration>(new IOException("native observation unavailable")) : Task.FromResult(_installed);
        public Task RecoverConfigurationAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task ApplyConfigurationAsync(NetworkConfiguration configuration, CancellationToken cancellationToken)
        {
            _installed = configuration;
            return Task.CompletedTask;
        }
    }

    private sealed class SuspendedClock : ISupervisorClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
        public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }
}
