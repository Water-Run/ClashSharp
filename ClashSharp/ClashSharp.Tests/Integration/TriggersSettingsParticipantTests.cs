extern alias ClashSharpUi;

using System.Threading.Channels;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Model.Triggers;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;
using TriggerFiredNotificationAdapter = ClashSharpUi::ClashSharp.Service.TriggerFiredNotificationAdapter;
using TriggerSettingsState = ClashSharpUi::ClashSharp.Hosting.Settings.TriggerSettingsState;
using TriggersSettingsParticipant = ClashSharpUi::ClashSharp.Hosting.Settings.TriggersSettingsParticipant;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises generation-owned trigger enablement against the real scheduler and durable settings session.</summary>
public sealed class TriggersSettingsParticipantTests
{
    [Fact]
    public async Task Facade_DrainsSettingsProducingEvaluationsBeforeTakingItsCommandGate()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        TaskCompletionSource entered = Signal();
        TaskCompletionSource continueProducer = Signal();
        TaskCompletionSource cancelledBeforePublication = Signal();
        TaskCompletionSource<Task<SettingsAuthorityResult>> nestedStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource paused = Signal();
        using CancellationTokenSource cleanupCancellation = new();
        fixture.Events.OnUnsubscribe = () => paused.TrySetResult();
        fixture.Evaluator.Handler = async () =>
        {
            using MutationAdmissionLease producerLease = fixture.Admission.AcquireOrdinary();
            entered.TrySetResult();
            await continueProducer.Task;
            try
            {
                Task<SettingsAuthorityResult> nested = fixture.Authority.ApplyChangesAdmittedAsync(
                    [new(SettingsRegistry.Keys.NotificationEnabled, SettingsEnvelopeTestData.Value("NotificationEnabled", "false"))],
                    Guid.NewGuid(), producerLease, cleanupCancellation.Token);
                nestedStarted.TrySetResult(nested);
                await nested;
            }
            catch (OperationCanceledException) { cancelledBeforePublication.TrySetResult(); }
        };
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<SettingsAuthorityResult> disabling = fixture.Authority.ApplyChangesAsync(
            [new(SettingsRegistry.Keys.TriggersEnabled, SettingsEnvelopeTestData.Value("TriggersEnabled", "false"))],
            Guid.NewGuid(), CancellationToken.None);
        try
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            while (fixture.Admission.State == MutationAdmissionState.Open && !paused.Task.IsCompleted)
            {
                await Task.Delay(10, deadline.Token);
            }
            MutationAdmissionState admissionBeforeProducerSettled = fixture.Admission.State;
            bool pausedBeforeProducerSettled = paused.Task.IsCompleted;
            continueProducer.TrySetResult();
            Task<SettingsAuthorityResult> nested = await nestedStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            if (pausedBeforeProducerSettled)
            {
                Assert.False(disabling.IsCompleted);
                Assert.False(nested.IsCompleted);
            }
            Assert.Equal(MutationAdmissionState.Closing, admissionBeforeProducerSettled);
            Assert.False(pausedBeforeProducerSettled);
            Assert.True((await disabling.WaitAsync(TimeSpan.FromSeconds(10))).IsSucceeded);
            Assert.True(cancelledBeforePublication.Task.IsCompleted);
            Assert.True(fixture.Authority.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.NotificationEnabled].Value.Get<bool>());
            Assert.False(fixture.Authority.CaptureSnapshot().Envelope.Applied[SettingsRegistry.Keys.TriggersEnabled].Value!.Get<bool>());
            Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        }
        finally
        {
            // A failing assertion against the previous ordering must still break and drain its cycle.
            continueProducer.TrySetResult();
            cleanupCancellation.Cancel();
            await disabling.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UninitializedScheduler_CannotClaimAnAppliedSettingOrStartStorageWork(bool target)
    {
        await using Fixture fixture = await Fixture.CreateAsync(target, start: false);
        Assert.Equal(0, fixture.Events.Subscriptions);
        Assert.Equal(0, fixture.Clock.Waits);
        SettingsAuthorityResult result = await fixture.ApplyAsync();
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal("settings.application.probe_failed", result.Code);
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(result.Envelope!.PendingApplications).State);
        Assert.Equal(SettingAppliedStateKind.Unknown, result.Envelope.Applied[SettingsRegistry.Keys.TriggersEnabled].Kind);
        Assert.False(fixture.Participant.Scheduler.IsRunning);
        Assert.Equal(0, fixture.Events.Subscriptions);
        Assert.Equal(0, fixture.Clock.Waits);
    }

    [Fact]
    public async Task AlreadyDisabledScheduler_IsObservedWithoutRestartingItsMaintenanceLoop()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false);
        SettingsAuthorityResult result = await fixture.ApplyAsync();
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal(1, fixture.Events.Subscriptions);
        Assert.True(fixture.Participant.Scheduler.IsRunning);
        Assert.False(result.Envelope!.Applied[SettingsRegistry.Keys.TriggersEnabled].Value!.Get<bool>());
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        await fixture.DrainAsync();
        Assert.Equal(0, fixture.Evaluator.Calls);
    }

    [Fact]
    public async Task DesiredChanges_ReachEvaluationOnlyAfterOwnedApplicationAndRemainRestartable()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        await fixture.DrainAsync();
        Assert.Equal(0, fixture.Evaluator.Calls);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        Assert.Equal(TriggerEventKind.ProxyStarted, (await fixture.Evaluator.ReadAsync()).EventKind);

        await fixture.ChangeAsync(false);
        Assert.False((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.Desired[SettingsRegistry.Keys.TriggersEnabled].Value.Get<bool>());
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        Assert.Equal(TriggerEventKind.AppEntered, (await fixture.Evaluator.ReadAsync()).EventKind);
        SettingsAuthorityResult disabled = await fixture.ApplyAsync();
        Assert.True(disabled.IsSucceeded, disabled.Code);
        Assert.False(disabled.Envelope!.Applied[SettingsRegistry.Keys.TriggersEnabled].Value!.Get<bool>());
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        await fixture.DrainAsync();
        Assert.Equal(2, fixture.Evaluator.Calls);

        await fixture.ChangeAsync(true);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        Assert.Equal(TriggerEventKind.AppEntered, (await fixture.Evaluator.ReadAsync()).EventKind);
        Assert.Equal(1, fixture.Events.ActiveSubscriptions);
    }

    [Fact]
    public async Task Disable_DrainsInflightAndQueuedEvaluationsDespitePageCancellation()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        fixture.Evaluator.BlockFirst = true;
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        await fixture.Evaluator.ReadAsync();
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        await fixture.ChangeAsync(false);
        TaskCompletionSource paused = Signal();
        fixture.Events.OnUnsubscribe = () => paused.TrySetResult();
        using CancellationTokenSource cancellation = new();
        Task<SettingsAuthorityResult> applying = fixture.ApplyAsync(cancellationToken: cancellation.Token);
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.False(applying.IsCompleted);
        Assert.Throws<MutationAdmissionRejectedException>(() => fixture.Admission.AcquireOrdinary());
        Assert.False(fixture.Evaluator.CancellationObserved.Task.IsCompleted);
        fixture.Evaluator.Release.TrySetResult();
        Assert.True((await applying).IsSucceeded);
        using MutationAdmissionLease reopened = fixture.Admission.AcquireOrdinary();
        Assert.Equal(TriggerEventKind.ProxyStarted, (await fixture.Evaluator.ReadAsync()).EventKind);
        Assert.Equal(2, fixture.Evaluator.Calls);
        SettingsEnvelope durable = (await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!;
        Assert.Empty(durable.PendingApplications);
        Assert.False(durable.Applied[SettingsRegistry.Keys.TriggersEnabled].Value!.Get<bool>());
        Assert.True(fixture.Participant.Scheduler.IsRunning);
    }

    [Fact]
    public async Task LostApplicationReply_IsResolvedFromTheActualInstalledSchedulerConfiguration()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        SettingsAuthorityResult result = await fixture.ApplyAsync(new ObservingParticipant(fixture.Participant) { LoseReply = true });
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal("settings.application.reply_lost_resolved", result.Code);
        Assert.Equal(SettingAppliedValueSource.RuntimeProbe, result.Envelope!.Applied[SettingsRegistry.Keys.TriggersEnabled].Source);
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        await fixture.Evaluator.ReadAsync();
        Assert.Equal(1, fixture.Evaluator.Calls);
    }

    [Fact]
    public async Task FailedResume_RetainsUnknownIntentUntilRuntimeRecoveryAndExplicitRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        fixture.Events.SubscribeFailure = new IOException("Isolated event source is unavailable.");
        SettingsAuthorityResult result = await fixture.ApplyAsync();
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.False(fixture.Participant.Scheduler.IsRunning);
        Assert.Equal(SettingAppliedStateKind.Unknown, result.Envelope!.Applied[SettingsRegistry.Keys.TriggersEnabled].Kind);
        SettingsApplicationBatch failed = Assert.Single(result.Envelope.PendingApplications);
        Assert.Equal(SettingsApplicationBatchState.Failed, failed.State);
        Assert.True(result.Envelope.Desired[SettingsRegistry.Keys.TriggersEnabled].Value.Get<bool>());

        fixture.Events.SubscribeFailure = null;
        await fixture.Participant.Scheduler.StartAsync(CancellationToken.None);
        using (MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary())
        {
            Assert.True((await fixture.Session.RetryAdmittedAsync(failed.BatchId, failed.AttemptId,
                Guid.NewGuid(), lease, CancellationToken.None)).IsSucceeded);
        }
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        await fixture.Evaluator.ReadAsync();
        Assert.Equal(1, fixture.Events.ActiveSubscriptions);
    }

    [Fact]
    public async Task FatalResumeExceptionGraph_EscapesWithItsIdentityAndLeavesTheDurableAttemptRunning()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        InvalidOperationException fatal = new("Isolated wrapper.", new AggregateException(Activator.CreateInstance<OutOfMemoryException>()));
        fixture.Events.SubscribeFailure = fatal;
        Assert.Same(fatal, await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ApplyAsync()));
        SettingsEnvelope durable = (await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!;
        Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single(durable.PendingApplications).State);
        Assert.Equal(SettingAppliedStateKind.Unknown, durable.Applied[SettingsRegistry.Keys.TriggersEnabled].Kind);
    }

    [Fact]
    public async Task DisabledEvaluation_ContinuesRetryingPreviouslyReleasedLifecycleHandoffs()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        TriggerExecution execution = new(Guid.NewGuid(), "exit-task", 1, DateTimeOffset.UnixEpoch,
            Guid.NewGuid(), TriggerExecutionState.HandedOff);
        fixture.Evaluator.Execution = execution;
        fixture.Handoff.FailuresRemaining = 3;
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        await fixture.Handoff.ThirdAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await fixture.ChangeAsync(false);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        await fixture.Clock.TickAsync();
        Assert.Same(execution, await fixture.Handoff.ReadAsync());
        Assert.Equal(4, fixture.Handoff.Attempts);
        Assert.Equal(1, fixture.Evaluator.Calls);
    }

    [Fact]
    public async Task ForeignGenerationOrInactiveAdmission_IsRejectedBeforeSchedulerAccess()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        ObservingParticipant capture = new(fixture.Participant);
        Assert.True((await fixture.ApplyAsync(capture)).IsSucceeded);
        SettingsApplicationRequest request = Assert.IsType<SettingsApplicationRequest>(capture.Request);
        int subscriptions = fixture.Events.Subscriptions;
        using MutationAdmissionLease foreign = new MutationAdmissionBarrier().AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ProbeAsync(request, foreign, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ApplyAsync(request, foreign, CancellationToken.None));
        MutationAdmissionLease retired = fixture.Admission.AcquireOrdinary();
        retired.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ApplyAsync(request, retired, CancellationToken.None));
        DataGenerationDescriptor wrongGeneration = fixture.Directory.CreateGeneration(2);
        await using TriggersSettingsParticipant wrong = new(wrongGeneration, fixture.Admission,
            new TriggerSettingsState(wrongGeneration), fixture.Events, fixture.Clock, fixture.Evaluator, fixture.Handoff);
        using MutationAdmissionLease own = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.ProbeAsync(request, own, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.ApplyAsync(request, own, CancellationToken.None));
        Assert.Equal(subscriptions, fixture.Events.Subscriptions);
        Assert.Equal(1, fixture.Events.ActiveSubscriptions);
    }

    [Fact]
    public async Task CancelledApplication_DoesNotPauseTheSchedulerOrReplaceItsInstalledSetting()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        ObservingParticipant capture = new(fixture.Participant);
        Assert.True((await fixture.ApplyAsync(capture)).IsSucceeded);
        SettingsApplicationRequest request = Assert.IsType<SettingsApplicationRequest>(capture.Request);
        int subscriptions = fixture.Events.Subscriptions;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using MutationAdmissionLease own = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Participant.ApplyAsync(request, own, cancellation.Token));
        Assert.Equal(subscriptions, fixture.Events.Subscriptions);
        Assert.True(fixture.Participant.Scheduler.IsAcceptingEvents);
        Assert.True(Assert.Single((await fixture.Participant.ProbeAsync(request, own, CancellationToken.None)).Values).Value.Get<bool>());
    }

    [Fact]
    public async Task Retirement_WaitsForActiveEvaluationAndRejectsLaterReadsAndWrites()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        ObservingParticipant capture = new(fixture.Participant);
        Assert.True((await fixture.ApplyAsync(capture)).IsSucceeded);
        fixture.Evaluator.BlockFirst = true;
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        await fixture.Evaluator.ReadAsync();
        Task retiring = fixture.Participant.DisposeAsync().AsTask();
        await fixture.Evaluator.CancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(retiring.IsCompleted);
        Assert.Equal(0, fixture.Events.ActiveSubscriptions);
        fixture.Evaluator.Release.TrySetResult();
        await retiring;
        Assert.False(fixture.Participant.Scheduler.IsRunning);
        using MutationAdmissionLease lease = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Participant.ProbeAsync(capture.Request!, lease, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Participant.ApplyAsync(capture.Request!, lease, CancellationToken.None));
    }

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Theory]
    [InlineData("TriggersEnabled")]
    [InlineData("TriggerNotificationsEnabled")]
    public async Task AdmittedTriggerChange_RejectsOrdinaryAuthorityBeforePublishingDesiredIntent(string key)
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        ObservingParticipant capture = new(fixture.Participant);
        Assert.True((await fixture.ApplyAsync(capture)).IsSucceeded);
        long revision = fixture.Authority.CaptureSnapshot().Envelope.EnvelopeRevision;
        int subscriptions = fixture.Events.Subscriptions;
        using MutationAdmissionLease ordinary = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Authority.ApplyChangesAdmittedAsync(
            [new(new SettingKey(key), SettingsEnvelopeTestData.Value(key, "false"))],
            Guid.NewGuid(), ordinary, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ProbeAsync(capture.Request!, ordinary, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ApplyAsync(capture.Request!, ordinary, CancellationToken.None));
        Assert.Equal(revision, (await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.EnvelopeRevision);
        Assert.True(fixture.Authority.CaptureSnapshot().Envelope.Desired[new SettingKey(key)].Value.Get<bool>());
        Assert.Equal(subscriptions, fixture.Events.Subscriptions);
    }

    [Fact]
    public async Task FacadeRetry_AcquiresExclusiveAuthorityForTheResolvedTriggerBatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true, start: false);
        SettingsAuthorityResult failed = await fixture.ApplyAsync();
        SettingsApplicationBatch batch = Assert.Single(failed.Envelope!.PendingApplications);
        await fixture.Participant.Scheduler.StartAsync(CancellationToken.None);
        SettingsAuthorityResult retried = await fixture.Authority.RetryAsync(batch.BatchId, batch.AttemptId,
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(retried.IsSucceeded, retried.Code);
        Assert.Empty(retried.Envelope!.PendingApplications);
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        await fixture.Evaluator.ReadAsync();
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task FacadeRevert_VerifiesTheSafeDisabledFallbackUnderExclusiveAuthority()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        SettingsAuthorityResult result = await fixture.Authority.RevertAsync([SettingsRegistry.Keys.TriggersEnabled],
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.False(result.Envelope!.Desired[SettingsRegistry.Keys.TriggersEnabled].Value.Get<bool>());
        Assert.False(result.Envelope.Applied[SettingsRegistry.Keys.TriggersEnabled].Value!.Get<bool>());
        Assert.Empty(result.Envelope.PendingApplications);
        Assert.True(fixture.Participant.Scheduler.IsRunning);
        Assert.Equal(1, fixture.Events.Subscriptions);
    }

    [Fact]
    public async Task NotificationOnlyChange_DrainsEvaluationAndPreservesGlobalEnablement()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        int subscriptions = fixture.Events.Subscriptions;
        fixture.Evaluator.BlockFirst = true;
        fixture.Events.Publish(TriggerEventKind.AppEntered);
        await fixture.Evaluator.ReadAsync();
        Task<SettingsAuthorityResult> applying = fixture.Authority.ApplyChangesAsync(
            [new(SettingsRegistry.Keys.TriggerNotificationsEnabled, SettingsEnvelopeTestData.Value("TriggerNotificationsEnabled", "false"))],
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(fixture.Participant.Scheduler.IsRunning);
        Assert.False(applying.IsCompleted);
        Assert.False(fixture.Evaluator.CancellationObserved.Task.IsCompleted);
        fixture.Evaluator.Release.TrySetResult();
        SettingsAuthorityResult result = await applying.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result.IsSucceeded, result.Code);
        Assert.False(result.Envelope!.Applied[SettingsRegistry.Keys.TriggerNotificationsEnabled].Value!.Get<bool>());
        Assert.True(result.Envelope.Applied[SettingsRegistry.Keys.TriggersEnabled].Value!.Get<bool>());
        Assert.Equal(subscriptions + 1, fixture.Events.Subscriptions);
        await fixture.NotifyAsync();
        Assert.False(Assert.Single(fixture.DeliveredPolicies));
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        Assert.Equal(TriggerEventKind.ProxyStarted, (await fixture.Evaluator.ReadAsync()).EventKind);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
    }

    [Fact]
    public async Task CombinedTriggerChange_VerifiesEveryKeyInTheRegistryBatch()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        SettingsAuthorityResult result = await fixture.Authority.ApplyChangesAsync(
            [new(SettingsRegistry.Keys.TriggersEnabled, SettingsEnvelopeTestData.Value("TriggersEnabled", "true")),
             new(SettingsRegistry.Keys.TriggerNotificationsEnabled, SettingsEnvelopeTestData.Value("TriggerNotificationsEnabled", "false"))],
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.True(result.Envelope!.Applied[SettingsRegistry.Keys.TriggersEnabled].Value!.Get<bool>());
        Assert.False(result.Envelope.Applied[SettingsRegistry.Keys.TriggerNotificationsEnabled].Value!.Get<bool>());
        Assert.Empty(result.Envelope.PendingApplications);
        await fixture.NotifyAsync();
        Assert.False(Assert.Single(fixture.DeliveredPolicies));
        fixture.Events.Publish(TriggerEventKind.ProxyStarted);
        Assert.Equal(TriggerEventKind.ProxyStarted, (await fixture.Evaluator.ReadAsync()).EventKind);
    }

    [Fact]
    public async Task NotificationIntent_ChangesDeliveryOnlyAfterApplicationAndResolvesALostReply()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        using (MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary())
        {
            Assert.True((await fixture.Session.ChangeAdmittedAsync(
                [new(SettingsRegistry.Keys.TriggerNotificationsEnabled, SettingsEnvelopeTestData.Value("TriggerNotificationsEnabled", "false"))],
                Guid.NewGuid(), lease, CancellationToken.None)).IsSucceeded);
        }
        await fixture.NotifyAsync();
        Assert.True(Assert.Single(fixture.DeliveredPolicies));
        SettingsAuthorityResult result = await fixture.ApplyAsync(new ObservingParticipant(fixture.Participant) { LoseReply = true });
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal("settings.application.reply_lost_resolved", result.Code);
        await fixture.NotifyAsync();
        Assert.Equal([true, false], fixture.DeliveredPolicies);
        Assert.True(fixture.Settings.IsEnabled);
        await fixture.Participant.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.NotifyAsync());
    }

    [Fact]
    public async Task ForeignConsumerConfiguration_IsRejectedBeforeSchedulingStarts()
    {
        await using Fixture fixture = await Fixture.CreateAsync(false, start: false);
        await using DataGenerationTestDirectory foreignDirectory = new();
        var foreign = await foreignDirectory.PromoteFirstAsync();
        Assert.Throws<ArgumentException>(() => new TriggersSettingsParticipant(fixture.Session.Generation, fixture.Admission,
            new TriggerSettingsState(foreign.Descriptor), fixture.Events, fixture.Clock, fixture.Evaluator, fixture.Handoff));
        Assert.Equal(0, fixture.Events.Subscriptions);
        Assert.Equal(0, fixture.Clock.Waits);
    }

    [Fact]
    public async Task NotificationChange_PreservesAnUnrelatedPendingSchedulerIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync(true);
        SettingsAuthorityResult result = await fixture.Authority.ApplyChangesAsync(
            [new(SettingsRegistry.Keys.TriggerNotificationsEnabled, SettingsEnvelopeTestData.Value("TriggerNotificationsEnabled", "false"))],
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.False(fixture.Settings.IsEnabled);
        Assert.False(fixture.Settings.NotificationsEnabled);
        Assert.True(result.Envelope!.Desired[SettingsRegistry.Keys.TriggersEnabled].Value.Get<bool>());
        Assert.Equal(SettingsRegistry.Keys.TriggersEnabled,
            Assert.Single(Assert.Single(result.Envelope.PendingApplications).Entries).Key);
        Assert.Equal(MutationAdmissionState.Open, fixture.Admission.State);
        Assert.True((await fixture.ApplyAsync()).IsSucceeded);
        Assert.True(fixture.Settings.IsEnabled);
        Assert.False(fixture.Settings.NotificationsEnabled);
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private bool _generationInitialized;
        public DataGenerationTestDirectory Directory { get; } = new();
        public MutationAdmissionBarrier Admission { get; } = new();
        public Events Events { get; } = new();
        public Clock Clock { get; } = new();
        public Evaluator Evaluator { get; } = new();
        public Handoff Handoff { get; } = new();
        public JsonSettingsRepository Repository { get; private set; } = null!;
        public SettingsAuthoritySession Session { get; private set; } = null!;
        public TriggerSettingsState Settings { get; private set; } = null!;
        public TriggersSettingsParticipant Participant { get; private set; } = null!;
        public TriggerFiredNotificationAdapter Notifications { get; private set; } = null!;
        public List<bool> DeliveredPolicies { get; } = [];
        public DataGenerationManager Generations { get; } = new();
        public GenerationSettingsAuthority Authority { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(bool target, bool start = true)
        {
            Fixture fixture = new();
            try
            {
                var manifest = await fixture.Directory.PromoteFirstAsync();
                fixture.Repository = new(manifest.Descriptor, SettingsRegistry.Default);
                Assert.True((await fixture.Repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
                SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope([("TriggersEnabled", target ? "true" : "false")]);
                Dictionary<SettingKey, SettingAppliedState> applied = pending.Applied.ToDictionary();
                applied[SettingsRegistry.Keys.TriggersEnabled] = SettingAppliedState.Unknown(SettingAppliedUnknownReason.NotObserved, SettingAppliedUnknownHandling.QueueApplication);
                Assert.True((await fixture.Repository.SaveAsync(new(pending.SchemaVersion, pending.EnvelopeRevision,
                    pending.Desired, applied, pending.PendingApplications, pending.MigrationHistory), 1, CancellationToken.None)).IsSucceeded);
                fixture.Session = new(fixture.Repository, SettingsRegistry.Default, fixture.Admission);
                fixture.Settings = new(manifest.Descriptor);
                fixture.Notifications = new(() => fixture.Settings.NotificationsEnabled, new EmptyDefinitions(),
                    (_, _, enabled, _) => { fixture.DeliveredPolicies.Add(enabled); return Task.CompletedTask; }, (_, _) => { });
                fixture.Participant = new(manifest.Descriptor, fixture.Admission, fixture.Settings,
                    fixture.Events, fixture.Clock, fixture.Evaluator, fixture.Handoff);
                fixture.Generations.Initialize(manifest, new(manifest.Descriptor, new Lifetime(fixture)));
                fixture._generationInitialized = true;
                fixture.Authority = new(fixture.Generations, fixture.Admission);
                if (start) { await fixture.Participant.Scheduler.StartAsync(CancellationToken.None); }
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task ChangeAsync(bool target)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            Assert.True((await Session.ChangeAdmittedAsync([new(SettingsRegistry.Keys.TriggersEnabled, SettingsEnvelopeTestData.Value("TriggersEnabled", target ? "true" : "false"))],
                Guid.NewGuid(), lease, CancellationToken.None)).IsSucceeded);
        }

        public Task NotifyAsync() => Notifications.NotifyAsync(new(Guid.NewGuid(), "isolated-task", 1,
            DateTimeOffset.UnixEpoch, Guid.NewGuid(), TriggerExecutionState.Pending), CancellationToken.None);

        public async Task<SettingsAuthorityResult> ApplyAsync(ISettingsApplicationParticipant? participant = null, CancellationToken cancellationToken = default)
        {
            using MutationAdmissionLease lease = await Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken);
            SettingsApplicationBatch batch = Assert.Single((await Repository.OpenAsync(cancellationToken)).Envelope!.PendingApplications);
            return await Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId, participant ?? Participant,
                SettingsApplicationPhase.Live, lease, cancellationToken);
        }

        public async Task DrainAsync()
        {
            QuiescedState prior = await Participant.Scheduler.QuiesceAsync(CancellationToken.None);
            await Participant.Scheduler.ResumeAsync(prior, CancellationToken.None);
        }

        public async ValueTask DisposeAsync()
        {
            Evaluator.Release.TrySetResult();
            if (_generationInitialized) { await Generations.DisposeAsync(); }
            else
            {
                if (Session is not null) { await Session.DisposeAsync(); }
                if (Participant is not null) { await Participant.DisposeAsync(); }
            }
            await Directory.DisposeAsync();
        }

        private sealed class Lifetime(Fixture fixture) : IServiceProvider, IAsyncDisposable
        {
            private readonly SettingsGenerationContext _context = new(fixture.Session, [fixture.Participant]);
            public object? GetService(Type serviceType) => serviceType == typeof(SettingsGenerationContext) ? _context : null;
            public async ValueTask DisposeAsync()
            {
                await fixture.Session.DisposeAsync();
                await fixture.Participant.DisposeAsync();
            }
        }
    }

    private sealed class EmptyDefinitions : ITriggerDefinitionStore
    {
        public TriggerDefinitionCatalog Current { get; } = new(0, [], []);
        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(long expectedGeneration,
            IReadOnlyList<TriggerTaskDefinition> definitions, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ObservingParticipant(TriggersSettingsParticipant inner) : ISettingsApplicationParticipant
    {
        public bool LoseReply { get; init; }
        public SettingsApplicationRequest? Request { get; private set; }
        public SettingApplicationKind ApplicationKind => inner.ApplicationKind;
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken cancellationToken)
        { Request = request; return inner.ProbeAsync(request, lease, cancellationToken); }
        public async Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken cancellationToken)
        {
            await inner.ApplyAsync(request, lease, cancellationToken);
            if (LoseReply) { throw new IOException("Isolated lost application reply."); }
        }
    }

    private sealed class Events : ITriggerSchedulerEventSource
    {
        private EventHandler<TriggerSchedulerEvent>? _raised;
        public int Subscriptions { get; private set; }
        public int ActiveSubscriptions => _raised?.GetInvocationList().Length ?? 0;
        public Exception? SubscribeFailure { get; set; }
        public Action? OnUnsubscribe { get; set; }
        public event EventHandler<TriggerSchedulerEvent>? EventRaised
        {
            add { if (SubscribeFailure is not null) { throw SubscribeFailure; } ++Subscriptions; _raised += value; }
            remove { _raised -= value; OnUnsubscribe?.Invoke(); }
        }
        public void Publish(TriggerEventKind kind) => _raised?.Invoke(this, new(kind));
    }

    private sealed class Clock : ITriggerSchedulerClock
    {
        private readonly Channel<TaskCompletionSource> _ticks = Channel.CreateUnbounded<TaskCompletionSource>();
        private int _waits;
        public int Waits => Volatile.Read(ref _waits);
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public async Task WaitForNextTickAsync(CancellationToken cancellationToken)
        {
            TaskCompletionSource tick = Signal();
            using CancellationTokenRegistration registration = cancellationToken.Register(() => tick.TrySetCanceled(cancellationToken));
            Interlocked.Increment(ref _waits);
            Assert.True(_ticks.Writer.TryWrite(tick));
            await tick.Task;
        }
        public async Task TickAsync()
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            while (!(await _ticks.Reader.ReadAsync(deadline.Token)).TrySetResult()) { }
        }
    }

    private sealed class Evaluator : ITriggerSchedulerEvaluator
    {
        private readonly Channel<TriggerSchedulerEvent> _calls = Channel.CreateUnbounded<TriggerSchedulerEvent>();
        private int _count;
        public int Calls => Volatile.Read(ref _count);
        public bool BlockFirst { get; set; }
        public Func<Task>? Handler { get; set; }
        public TriggerExecution? Execution { get; set; }
        public TaskCompletionSource Release { get; } = Signal();
        public TaskCompletionSource CancellationObserved { get; } = Signal();
        public async Task<TriggerSchedulerEvaluationOutcome> EvaluateAsync(TriggerSchedulerEvent value, CancellationToken cancellationToken)
        {
            int ordinal = Interlocked.Increment(ref _count);
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            Assert.True(_calls.Writer.TryWrite(value));
            if (BlockFirst && ordinal == 1) { await Release.Task; }
            if (Handler is not null) { await Handler(); }
            return Execution is null ? TriggerSchedulerEvaluationOutcome.Succeeded() : TriggerSchedulerEvaluationOutcome.Succeeded([Execution]);
        }
        public async Task<TriggerSchedulerEvent> ReadAsync()
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            return await _calls.Reader.ReadAsync(deadline.Token);
        }
    }

    private sealed class Handoff : ITriggerLifecycleHandoff
    {
        private readonly Channel<TriggerExecution> _completed = Channel.CreateUnbounded<TriggerExecution>();
        public int FailuresRemaining { get; set; }
        public int Attempts { get; private set; }
        public TaskCompletionSource ThirdAttempt { get; } = Signal();
        public Task<TriggerActionProbeResult> ProbeAsync(TriggerOutboxAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<TriggerActionApplyResult> HandOffAsync(TriggerOutboxAction action, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcknowledgeReleaseAsync(TriggerLifecycleHandoffIdentity identity, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task AcknowledgeReleasedExecutionAsync(TriggerExecution execution, CancellationToken cancellationToken)
        {
            if (++Attempts == 3) { ThirdAttempt.TrySetResult(); }
            if (Attempts <= FailuresRemaining) { throw new IOException("Isolated acknowledgement unavailable."); }
            Assert.True(_completed.Writer.TryWrite(execution));
            return Task.CompletedTask;
        }
        public async Task<TriggerExecution> ReadAsync()
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            return await _completed.Reader.ReadAsync(deadline.Token);
        }
    }
}
