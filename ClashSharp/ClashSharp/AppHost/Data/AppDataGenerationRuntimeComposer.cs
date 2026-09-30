using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Hosting.Settings;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Constructs the real settings participants and paused producers over one opened repository container.</summary>
internal sealed class AppDataGenerationRuntimeComposer(
    MutationAdmissionBarrier admission,
    IRuntimeSettingsAuthority authority,
    Func<OwnedUiDispatcher> createDispatcher,
    IAppearanceNativeSettings appearance,
    StartupLaunchService startup,
    MihomoConnectionService connections,
    RuntimeTrafficRateService traffic,
    NetworkTakeoverService takeover,
    WindowsProxyService proxy,
    MihomoServiceManager service,
    NotificationService notifications,
    ITriggerRuntimeEventSource events,
    IApplicationLifetimeRequestSink lifetime,
    Func<bool> exitRequested,
    TimeProvider time,
    Guid processEpoch,
    Func<string, string> getString,
    Func<CoreConfigurationService, INetworkSettingsRuntime>? createNetwork = null,
    ISupervisorClock? producerClock = null)
{
    public Task ComposeAsync(AppDataGenerationRepositories repositories, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = repositories.Generation;
        repositories.OwnSettingsParticipant(new InternalSettingsParticipant(generation, admission, SettingsRegistry.Default));
        repositories.OwnSettingsParticipant(new AppearanceSettingsParticipant(generation, admission, SettingsRegistry.Default, createDispatcher(), appearance));
        repositories.OwnSettingsParticipant(new StartupTaskSettingsParticipant(generation, admission, startup));
        ConnectionSamplingService sampling = repositories.OwnProducer(new ConnectionSamplingService(
            new SamplingPreferences(repositories.Session), new ConnectionSamplingSourceAdapter(connections),
            new ConnectionSamplingStorageAdapter(repositories.Logs), getString, producerClock));
        repositories.OwnSettingsParticipant(new SamplingSettingsParticipant(generation, admission, sampling));
        INetworkSettingsRuntime network = createNetwork?.Invoke(repositories.Configuration)
            ?? new NetworkSettingsRuntime(repositories.Configuration, takeover, proxy);
        repositories.OwnSettingsParticipant(new NetworkSettingsParticipant(generation, admission, network));
        INetworkStateObserver observer = new NetworkObserver(network, proxy);
        TriggerDefinitionStore definitions = new(repositories.Triggers, time);
        TriggerSettingsState triggerState = new(generation);
        TriggerLifecycleHandoffCoordinator handoff = new(repositories.Triggers, lifetime, time, processEpoch);
        TriggerActionRuntimeAdapter actions = new(authority, startup, sampling, connections, observer, notifications, handoff, service);
        TriggerFiredNotificationAdapter fired = new(() => triggerState.NotificationsEnabled, definitions,
            notifications.DeliverTriggerFiredNotificationAsync, notifications.ReportTriggerFiredNotificationFailure);
        ApplicationErrorSink errors = new(repositories.Logs.AppendLog, getString);
        TriggerActionExecutor executor = new(repositories.Triggers, actions, fired,
            new TriggerExecutionLogAdapter(definitions, getString, repositories.Logs.AppendLog, errors));
        TriggerContextProviderAdapter context = new(new SqliteTriggerTrafficContextSource(repositories.Logs.DatabasePath),
            new RuntimeTriggerContextSource(traffic), time, time.GetUtcNow());
        TriggerExecutionCoordinator executions = new(repositories.Triggers, new TriggerExecutionGate(), new TriggerEvaluator(context),
            admission, executor, time, processEpoch);
        TriggersSettingsParticipant triggers = new(generation, admission, triggerState, new TriggerSchedulerEventSourceAdapter(events),
            new SystemTriggerSchedulerClock(time, TimeSpan.FromSeconds(30)), new TriggerSchedulerEvaluator(repositories.Triggers, executions),
            handoff, health => ReportHealth(repositories.Logs, health));
        repositories.OwnSettingsParticipant(triggers);
        repositories.OwnProducer(triggers.Scheduler);
        ProfileSubscriptionScheduler subscriptions = repositories.OwnProducer(new ProfileSubscriptionScheduler(
            new ProfileSubscriptionSchedulerCatalogAdapter(repositories.Profiles), time, repositories.Logs.AppendLog, producerClock));
        repositories.AttachRuntime(new(repositories, admission, sampling, triggers, definitions,
            new TriggerActionReconciler(repositories.Triggers, executor, admission), executions, context, network, observer, subscriptions,
            handoff, processEpoch, exitRequested));
        return Task.CompletedTask;
    }

    private void ReportHealth(ILogStorage logs, SupervisorHealth health)
    {
        if (health.State is SupervisorHealthState.Retrying or SupervisorHealthState.Degraded)
        {
            logs.AppendLog("Warning", "Trigger", getString("Triggers.Log.RuntimeEventFailed"), health.ErrorCode);
        }
    }

    private sealed class SamplingPreferences(SettingsAuthoritySession session) : IConnectionSamplingSettings
    {
        public bool IsEnabled => session.Snapshot.Desired[SettingsRegistry.Keys.ConnectionSamplingEnabled].Value.Get<bool>();
        public int IntervalSeconds => session.Snapshot.Desired[SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds].Value.Get<int>();
    }

    private sealed class NetworkObserver(INetworkSettingsRuntime runtime, WindowsProxyService proxy) : INetworkStateObserver
    {
        public async Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken)
        {
            // The runtime read independently validates the owner, controller and complete
            // configuration. Preserve the separately observed Windows proxy flag as well.
            NetworkSettingsConfiguration actual = await runtime.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
            WindowsProxyState windows = proxy.GetCurrentState();
            return new(actual.Mode, actual.Mode != ClashSharpMode.Disabled, windows.IsEnabled, actual.EffectiveTunEnabled,
                actual.MixedPort, $"{actual.Mode}:{actual.ProfileId}:{actual.MixedPort}:{actual.EffectiveTunEnabled}:{windows.IsEnabled}");
        }
    }
}
