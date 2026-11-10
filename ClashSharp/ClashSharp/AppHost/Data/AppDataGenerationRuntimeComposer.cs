using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Agent;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Hosting.Agent;
using ClashSharp.Hosting.Settings;
using ClashSharp.Infrastructure.Agent;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Constructs the real settings participants and paused producers over one opened repository container.</summary>
internal sealed class AppDataGenerationRuntimeComposer(
    MutationAdmissionBarrier admission,
    DataGenerationManager generations,
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
    ISupervisorClock? producerClock = null,
    Func<ClashSharpMode, CancellationToken, Task>? publishModeApplied = null)
{
    public Task ComposeAsync(AppDataGenerationRepositories repositories, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = repositories.Generation;
        repositories.OwnSettingsParticipant(new InternalSettingsParticipant(generation, admission, SettingsRegistry.Default));
        OwnedUiDispatcher dispatcher = createDispatcher();
        repositories.OwnSettingsParticipant(new AppearanceSettingsParticipant(generation, admission, SettingsRegistry.Default, dispatcher, appearance));
        repositories.OwnSettingsParticipant(new StartupTaskSettingsParticipant(generation, admission, startup));
        ConnectionSamplingService sampling = repositories.OwnProducer(new ConnectionSamplingService(
            new SamplingPreferences(repositories.Session), new ConnectionSamplingSourceAdapter(connections, repositories.Configuration),
            new ConnectionSamplingStorageAdapter(repositories.Logs), getString, producerClock));
        repositories.OwnSettingsParticipant(new SamplingSettingsParticipant(generation, admission, sampling));
        NetworkTakeoverService scopedTakeover = takeover.BindDataScope(repositories.Configuration,
            repositories.ProxySelections ?? throw new InvalidOperationException("The generation has no proxy-selection owner."),
            traffic.RefreshAsync, sampling.FlushAsync, repositories.Logs, flushObservedSampling: sampling.FlushAsync);
        INetworkSettingsRuntime network = createNetwork?.Invoke(repositories.Configuration)
            ?? new NetworkSettingsRuntime(repositories.Configuration, scopedTakeover, proxy);
        repositories.OwnSettingsParticipant(new NetworkSettingsParticipant(generation, admission, network));
        GenerationExternalStateRecovery recovery = new(repositories.Session, admission, dispatcher, appearance, startup, network);
        INetworkStateObserver observer = new NetworkObserver(network);
        TriggerDefinitionStore definitions = new(repositories.Triggers, time);
        TriggerSettingsState triggerState = new(generation);
        GenerationPublicationGate publication = new();
        TriggerLifecycleHandoffCoordinator handoff = new(repositories.Triggers, lifetime, time, processEpoch);
        TriggerActionRuntimeAdapter actions = new(authority, startup, sampling, connections, observer, notifications, handoff, service);
        TriggerFiredNotificationAdapter fired = new(() => triggerState.NotificationsEnabled, definitions,
            notifications.DeliverTriggerFiredNotificationAsync, notifications.ReportTriggerFiredNotificationFailure);
        ApplicationErrorSink errors = new(repositories.Logs.AppendLog, getString);
        McpAccessState access = new();
        AppMcpAgentBackend agent = new(repositories, generations, admission, authority, observer, access, time.GetUtcNow(), publishModeApplied);
        McpRuntimeController mcpRuntime = new(admission, new McpProtectedCredentialStore(generation), new McpHttpServer(agent, errors), access);
        McpSettingsParticipant mcp = repositories.OwnProducer(new McpSettingsParticipant(generation, admission, SettingsRegistry.Default, mcpRuntime));
        repositories.OwnSettingsParticipant(mcp);
        TriggerActionExecutor executor = new(repositories.Triggers, actions, fired,
            new TriggerExecutionLogAdapter(definitions, getString, repositories.Logs.AppendLog, errors));
        TriggerContextProviderAdapter context = new(new SqliteTriggerTrafficContextSource(repositories.Logs.DatabasePath),
            new RuntimeTriggerContextSource(traffic), time, time.GetUtcNow());
        TriggerExecutionCoordinator executions = new(repositories.Triggers, new TriggerExecutionGate(), new TriggerEvaluator(context),
            admission, executor, time, processEpoch);
        TriggersSettingsParticipant triggers = new(generation, admission, triggerState, new TriggerSchedulerEventSourceAdapter(events),
            new SystemTriggerSchedulerClock(time, TimeSpan.FromSeconds(30)), new TriggerSchedulerEvaluator(repositories.Triggers, executions),
            handoff, health => ReportHealth(repositories.Logs, health), () => publication.IsPublished);
        repositories.OwnSettingsParticipant(triggers);
        repositories.OwnProducer(triggers.Scheduler);
        SubscriptionReminderMonitor reminders = new(() => ReadActiveSubscription(repositories), new LocalSubscriptionReminderStore(),
            notifications.Show, getString, new DisplayFormatter(() => ReadDisplayFormatting(repositories.Session)), time);
        ProfileSubscriptionScheduler subscriptions = repositories.OwnProducer(new ProfileSubscriptionScheduler(
            new ProfileSubscriptionSchedulerCatalogAdapter(repositories.Profiles, notifications), time, repositories.Logs.AppendLog, producerClock,
            waitForExecution: publication.WaitAsync, afterPass: reminders.Evaluate));
        repositories.AttachRuntime(new(repositories, admission, generations, sampling, scopedTakeover, triggers, definitions,
            new TriggerActionReconciler(repositories.Triggers, executor, admission), executions, context, network, observer, subscriptions,
            handoff, publication, recovery, mcp, processEpoch, exitRequested));
        return Task.CompletedTask;
    }

    /// <summary>Finds the subscription link behind the active profile, or null for a local or built-in profile.</summary>
    private static ProfileSubscriptionLink? ReadActiveSubscription(AppDataGenerationRepositories repositories)
    {
        string activeProfileId = repositories.Session.Snapshot.Desired[SettingsRegistry.Keys.ActiveProfileId].Value.Get<string>();
        if (string.IsNullOrEmpty(activeProfileId)) { return null; }
        foreach (ProfileSubscriptionLink link in repositories.Profiles.GetSubscriptionLinks())
        {
            if (StringComparer.Ordinal.Equals("subscription-" + link.Id, activeProfileId)) { return link; }
        }

        return null;
    }

    /// <summary>Reads the applied unit and clock preferences, so reminders use the same units as the window.</summary>
    private static DisplayFormattingOptions ReadDisplayFormatting(SettingsAuthoritySession session)
    {
        SettingsEnvelope envelope = session.Snapshot;
        return new(Read<DataUnitStyle>(SettingsRegistry.Keys.DataUnitStyle), Read<RateUnit>(SettingsRegistry.Keys.RateUnit),
            Read<ClockFormat>(SettingsRegistry.Keys.ClockFormat));

        T Read<T>(SettingKey key) where T : notnull => envelope.Applied[key].Value is { } value
            ? value.Get<T>() : SettingsRegistry.Default.Get(key.Value).DefaultValue.Get<T>();
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

    private sealed class NetworkObserver(INetworkSettingsRuntime runtime) : INetworkStateObserver
    {
        public Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken) =>
            runtime.ReadStateAsync(cancellationToken);
    }
}
