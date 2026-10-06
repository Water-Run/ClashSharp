extern alias ClashSharpUi;
using System.Runtime.CompilerServices;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model;
using ClashSharp.Model.Triggers;
using ClashSharp.Settings;
using ActionFailure = ClashSharpUi::ClashSharp.Service.SettingsActionFailedException;
using ActionKind = ClashSharpUi::ClashSharp.Model.ApplicationActionKind;
using Actions = ClashSharpUi::ClashSharp.Service.ApplicationActionService;
using Connections = ClashSharpUi::ClashSharp.Service.MihomoConnectionService;
using Events = ClashSharpUi::ClashSharp.Service.TriggerRuntimeEventHub;
using Lifecycle = ClashSharpUi::ClashSharp.Service.ApplicationLifecycleService;
using Notifications = ClashSharpUi::ClashSharp.Service.NotificationService;
using NotificationSink = ClashSharpUi::ClashSharp.Service.IApplicationNotificationSink;
using Sampling = ClashSharpUi::ClashSharp.Service.ConnectionSamplingService;
using Startup = ClashSharpUi::ClashSharp.Service.StartupLaunchService;
using TriggerRuntime = ClashSharpUi::ClashSharp.Service.TriggerActionRuntimeAdapter;

namespace ClashSharp.Tests.Integration;

public sealed partial class GenerationSettingsAuthorityTests
{
    [Theory]
    [InlineData("SetLaunchAtStartup", "true", "LaunchAtStartupEnabled")]
    [InlineData("SetConnectionSampling", "false", "ConnectionSamplingEnabled")]
    public async Task ProductionApplicationActions_UseTheCompleteAuthorityCommand(string kind, string value, string key)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        Actions actions = CreateActions(fixture, new ActionObserver(fixture));

        await actions.DispatchAsync(Enum.Parse<ActionKind>(kind), value, CancellationToken.None);

        Assert.Equal(value, fixture.Session.Snapshot.Desired[new(key)].Value.CanonicalText);
        Assert.Equal(value, fixture.Session.Snapshot.Applied[new(key)].Value!.CanonicalText);
        Assert.Empty(fixture.Session.Snapshot.PendingApplications);
        Assert.Equal(1, fixture.Participants[SettingsRegistry.Default.Get(key).ApplicationKind].Applies);
    }

    [Fact]
    public async Task ProductionNetworkAction_ReobservesCompanionsAndUsesActualResultFlags()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        fixture.Participants[SettingApplicationKind.Network].SetObserved(SettingsRegistry.Keys.MixedPort, Change("MixedPort", "23456").Value);
        Actions actions = CreateActions(fixture, new ActionObserver(fixture));

        NetworkTakeoverResult result = await actions.ApplyNetworkModeAsync(ClashSharpMode.RuleTakeover, CancellationToken.None);

        Assert.Equal(ClashSharpMode.RuleTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.True(result.TunRequested);
        Assert.True(result.TransparentProxyEnabled);
        Assert.False(result.SystemProxyEnabled);
        Assert.Equal(MihomoCoreOwner.Service, result.RequestedOwner);
        Assert.Equal("10000", fixture.Participants[SettingApplicationKind.Network].GetObserved(SettingsRegistry.Keys.MixedPort).CanonicalText);
        Assert.Equal("10000", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
    }

    [Fact]
    public async Task ProductionNetworkAction_ReappliesTheDesiredModeAfterObservedRuntimeLoss()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        Actions actions = CreateActions(fixture, new ActionObserver(fixture));
        _ = await actions.ApplyNetworkModeAsync(ClashSharpMode.RuleTakeover, CancellationToken.None);
        int priorApplies = fixture.Participants[SettingApplicationKind.Network].Applies;
        fixture.Participants[SettingApplicationKind.Network].SetObserved(
            SettingsRegistry.Keys.CurrentMode, Change("CurrentMode", "Disabled").Value);

        NetworkTakeoverResult result = await actions.ApplyNetworkModeAsync(ClashSharpMode.RuleTakeover, CancellationToken.None);

        Assert.True(result.CoreRunning);
        Assert.Equal(ClashSharpMode.RuleTakeover, result.Mode);
        Assert.Equal(priorApplies + 1, fixture.Participants[SettingApplicationKind.Network].Applies);
        Assert.Equal("RuleTakeover", fixture.Participants[SettingApplicationKind.Network].GetObserved(SettingsRegistry.Keys.CurrentMode).CanonicalText);
        Assert.Equal("RuleTakeover", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.CurrentMode].Value!.CanonicalText);
        Assert.Empty(fixture.Session.Snapshot.PendingApplications);
    }

    [Fact]
    public async Task ProductionNetworkAction_RetainsAdmissionThroughItsFinalNativeRead()
    {
        using CancellationTokenSource cancellation = new();
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ActionObserver observer = new(fixture) { BeforeRead = async () => { entered.TrySetResult(); await release.Task; } };
        Task<NetworkTakeoverResult> command = CreateActions(fixture, observer).ApplyNetworkModeAsync(ClashSharpMode.Standby, cancellation.Token);
        Task<MutationAdmissionLease>? drain = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            drain = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).AsTask();
            Assert.False(drain.IsCompleted);
            Assert.False(command.IsCompleted);
        }
        finally { release.TrySetResult(); }
        NetworkTakeoverResult result = await command.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(ClashSharpMode.Standby, result.Mode);
        Assert.True(result.CoreRunning);
        await using MutationAdmissionLease exclusive = await drain!.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("core")]
    [InlineData("proxy")]
    public async Task ProductionNetworkAction_DoesNotReturnSuccessFromAnUnknownOrContradictoryFinalRead(string fault)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        Assert.True((await fixture.Authority.ApplyChangesAsync(
            [Change("TransparentProxyEnabled", "false")], Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        ActionObserver observer = new(fixture)
        {
            Transform = state => fault switch
            {
                "unknown" => state with { IsKnown = false },
                "core" => state with { CoreRunning = false },
                _ => state with { SystemProxyEnabled = false },
            },
        };
        ActionFailure failure = await Assert.ThrowsAsync<ActionFailure>(() =>
            CreateActions(fixture, observer).ApplyNetworkModeAsync(ClashSharpMode.RuleTakeover, CancellationToken.None));
        Assert.Equal("settings.network_result_unverified", failure.DiagnosticCode);
        Assert.Equal("RuleTakeover", fixture.Session.Snapshot.Desired[SettingsRegistry.Keys.CurrentMode].Value.CanonicalText);
    }

    [Fact]
    public async Task ProductionNetworkAction_PreservesUnrelatedPendingProfileIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        using (MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary())
        {
            Assert.True((await fixture.Session.ChangeAdmittedAsync(
                [Change("ActiveProfileId", "pending-profile")], Guid.NewGuid(), lease, CancellationToken.None)).IsSucceeded);
        }
        SettingsApplicationBatch pending = Assert.Single(fixture.Session.Snapshot.PendingApplications);

        NetworkTakeoverResult result = await CreateActions(fixture, new ActionObserver(fixture))
            .ApplyNetworkModeAsync(ClashSharpMode.Standby, CancellationToken.None);

        Assert.Equal(ClashSharpMode.Standby, result.Mode);
        Assert.Equal("builtin-direct", fixture.Participants[SettingApplicationKind.Network].GetObserved(SettingsRegistry.Keys.ActiveProfileId).CanonicalText);
        Assert.Equal("pending-profile", fixture.Session.Snapshot.Desired[SettingsRegistry.Keys.ActiveProfileId].Value.CanonicalText);
        SettingsApplicationBatch retained = Assert.Single(fixture.Session.Snapshot.PendingApplications);
        Assert.Equal(pending.BatchId, retained.BatchId);
        Assert.Equal(pending.AttemptId, retained.AttemptId);
    }

    [Fact]
    public async Task ProductionNetworkAction_DetectsAConflictingCommandBeforeReturningTheOldResult()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        ActionObserver observer = new(fixture)
        {
            BeforeRead = async () => Assert.True((await fixture.Authority.ApplyRuntimeChangesAsync(
                [Change("CurrentMode", "Standby")], Guid.NewGuid(), CancellationToken.None)).IsSucceeded),
        };
        ActionFailure failure = await Assert.ThrowsAsync<ActionFailure>(() =>
            CreateActions(fixture, observer).ApplyNetworkModeAsync(ClashSharpMode.RuleTakeover, CancellationToken.None));
        Assert.Equal("settings.network_result_superseded", failure.DiagnosticCode);
        Assert.Equal("Standby", fixture.Session.Snapshot.Desired[SettingsRegistry.Keys.CurrentMode].Value.CanonicalText);
    }

    [Fact]
    public async Task ProductionNetworkAction_PreservesTheObservedForeignProxyFlagWhenTakeoverIsDisabled()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        ActionObserver observer = new(fixture) { Transform = state => state with { SystemProxyEnabled = true } };
        NetworkTakeoverResult result = await CreateActions(fixture, observer).ApplyNetworkModeAsync(ClashSharpMode.Disabled, CancellationToken.None);
        Assert.False(result.CoreRunning);
        Assert.False(result.TransparentProxyEnabled);
        Assert.True(result.SystemProxyEnabled);
    }

    [Theory]
    [InlineData(TriggerActionKind.SetLaunchAtStartup, "true", "LaunchAtStartupEnabled")]
    [InlineData(TriggerActionKind.SetConnectionSampling, "false", "ConnectionSamplingEnabled")]
    [InlineData(TriggerActionKind.SetTransparentProxy, "false", "TransparentProxyEnabled")]
    [InlineData(TriggerActionKind.SwitchProxyMode, "RuleTakeover", "CurrentMode")]
    public async Task ProductionTriggerActions_UseTheSuppliedLeaseAndRequireVerifiedEffects(TriggerActionKind kind, string value, string key)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        TriggerRuntime runtime = CreateTriggerRuntime(fixture);
        using MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary();

        TriggerActionApplyResult result = await runtime.ApplyAsync(SettingsAction(kind, value), lease, CancellationToken.None);

        Assert.Equal(TriggerActionApplyStatus.Applied, result.Status);
        fixture.Admission.EnsureActiveLease(lease);
        Assert.Equal(value, fixture.Session.Snapshot.Desired[new(key)].Value.CanonicalText);
        Assert.Equal(value, fixture.Session.Snapshot.Applied[new(key)].Value!.CanonicalText);
        Assert.Empty(fixture.Session.Snapshot.PendingApplications);
    }

    [Fact]
    public async Task ProductionTriggerAction_UnverifiedEffectAndRetainedFailedAttemptRemainUncertain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        fixture.Participants[SettingApplicationKind.StartupTask].IgnoreEffects = true;
        TriggerRuntime runtime = CreateTriggerRuntime(fixture);
        using MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary();
        TriggerOutboxAction action = SettingsAction(TriggerActionKind.SetLaunchAtStartup, "true");

        TriggerActionApplyResult first = await runtime.ApplyAsync(action, lease, CancellationToken.None);
        TriggerActionApplyResult second = await runtime.ApplyAsync(action, lease, CancellationToken.None);

        Assert.Equal(TriggerActionApplyStatus.Uncertain, first.Status);
        Assert.Equal(TriggerActionApplyStatus.Uncertain, second.Status);
        Assert.Equal(1, fixture.Participants[SettingApplicationKind.StartupTask].Applies);
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(fixture.Session.Snapshot.PendingApplications).State);
    }

    [Fact]
    public async Task ProductionModeDispatch_CancellationAfterCommitDoesNotDropItsCompletionNotification()
    {
        using CancellationTokenSource cancellation = new();
        PublicationHook hook = new();
        await using Fixture fixture = await Fixture.CreateAsync(hook);
        _ = await fixture.Authority.OpenAsync(CancellationToken.None);
        RecordedNotifications notifications = new();
        hook.AfterFirstPublication = cancellation.Cancel;

        await CreateActions(fixture, new ActionObserver(fixture), notifications)
            .DispatchAsync(ActionKind.SwitchProxyMode, "RuleTakeover", cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(ClashSharpMode.RuleTakeover, Assert.Single(notifications.Modes));
        Assert.Equal("RuleTakeover", fixture.Session.Snapshot.Applied[SettingsRegistry.Keys.CurrentMode].Value!.CanonicalText);
    }

    private static Actions CreateActions(Fixture fixture, INetworkStateObserver observer, NotificationSink? notifications = null,
        IApplicationDataClearOperationFactory? removal = null, Lifecycle? lifecycle = null) =>
        new(fixture.Authority, fixture.Admission, Unused<NetworkStateCoordinator>(), observer,
            Unused<Sampling>(), Unused<Connections>(), notifications ?? new RecordedNotifications(), new Events(),
            (_, _, _, _) => throw new InvalidOperationException("Unexpected logging in this action."),
            key => key, lifecycle ?? Unused<Lifecycle>(), Unused<RuntimeLifecycleCoordinator>(), Unused<Startup>(),
            new ActionCredential(), installAsPrimaryInstance: false, dataClearOperations: removal);

    private static TriggerRuntime CreateTriggerRuntime(Fixture fixture) =>
        new(fixture.Authority, Unused<Startup>(), Unused<Sampling>(), Unused<Connections>(),
            new ActionObserver(fixture), Unused<Notifications>(), new UnusedActionHandoff());

    // These dependencies must never be called by a settings action. Existing native-boundary tests
    // exercise their own behavior; an accidental return to a legacy path fails this tripwire.
    private static T Unused<T>() where T : class => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static TriggerOutboxAction SettingsAction(TriggerActionKind kind, string value)
    {
        Guid executionId = Guid.NewGuid();
        TriggerAction action = kind == TriggerActionKind.SwitchProxyMode
            ? new(kind, new ProxyModeActionParameters(Enum.Parse<ClashSharpMode>(value)))
            : new(kind, new BooleanActionParameters(bool.Parse(value)));
        return new(executionId, 1, 0, TriggerIdempotencyKey.Create(executionId, 1, 0), action, TriggerOutboxState.Pending);
    }

    private sealed class ActionObserver(Fixture fixture) : INetworkStateObserver
    {
        public Func<Task>? BeforeRead { get; init; }
        public Func<NetworkStateSnapshot, NetworkStateSnapshot>? Transform { get; init; }
        public async Task<NetworkStateSnapshot> ObserveAsync(CancellationToken cancellationToken)
        {
            Assert.False(cancellationToken.CanBeCanceled);
            if (BeforeRead is not null) { await BeforeRead(); }
            Participant runtime = fixture.Participants[SettingApplicationKind.Network];
            ClashSharpMode mode = runtime.GetObserved(SettingsRegistry.Keys.CurrentMode).Get<ClashSharpMode>();
            bool takeover = mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
            bool tun = takeover && runtime.GetObserved(SettingsRegistry.Keys.TransparentProxyEnabled).Get<bool>();
            NetworkStateSnapshot observed = new(mode, mode != ClashSharpMode.Disabled, takeover && !tun, tun,
                runtime.GetObserved(SettingsRegistry.Keys.MixedPort).Get<int>(), "isolated-native-observation");
            return Transform?.Invoke(observed) ?? observed;
        }
    }

    private sealed class ActionCredential : IControllerCredentialProvider
    {
        public string GetSecret() => "isolated-runtime-action-credential";
    }

    private sealed class RecordedNotifications : NotificationSink
    {
        public List<ClashSharpMode> Modes { get; } = [];
        public void NotifyProxyModeChanged(ClashSharpMode mode) => Modes.Add(mode);
        public void NotifyCustom(string message) => throw new NotSupportedException();
    }

    private sealed class UnusedActionHandoff : ITriggerLifecycleHandoff
    {
        public Task<TriggerActionProbeResult> ProbeAsync(TriggerOutboxAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TriggerActionApplyResult> HandOffAsync(TriggerOutboxAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcknowledgeReleaseAsync(TriggerLifecycleHandoffIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcknowledgeReleasedExecutionAsync(TriggerExecution execution, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
