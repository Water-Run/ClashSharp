using System.Threading.Channels;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Hosting.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises actual runtime adapters, the sampling supervisor, and durable settings with isolated platform ports.</summary>
public sealed class SettingsRuntimeParticipantTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupChange_ProbesWindowsAndPersistsOnlyTheVerifiedRegistration(bool target)
    {
        await using Fixture fixture = await Fixture.CreateAsync(("LaunchAtStartupEnabled", target ? "true" : "false"));
        fixture.Windows.State = target ? StartupLaunchTaskState.Disabled : StartupLaunchTaskState.Enabled;
        SettingsAuthorityResult result = await fixture.ApplyAsync(fixture.Startup);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal(target, result.Envelope.Applied[SettingsRegistry.Keys.LaunchAtStartupEnabled].Value!.Get<bool>());
        Assert.Equal(1, fixture.Windows.Mutations);
        Assert.True(fixture.Windows.Reads >= 4);
        Assert.Equal(target ? StartupLaunchTaskState.Enabled : StartupLaunchTaskState.Disabled, fixture.Windows.State);
    }

    [Fact]
    public async Task StartupAlreadyApplied_DoesNotRepeatTheWindowsMutation()
    {
        await using Fixture fixture = await Fixture.CreateAsync(("LaunchAtStartupEnabled", "true"));
        fixture.Windows.State = StartupLaunchTaskState.Enabled;
        SettingsAuthorityResult result = await fixture.ApplyAsync(fixture.Startup);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal(0, fixture.Windows.Mutations);
        Assert.Equal(SettingAppliedValueSource.RuntimeProbe,
            result.Envelope!.Applied[SettingsRegistry.Keys.LaunchAtStartupEnabled].Source);
    }

    [Fact]
    public async Task StartupDenied_RetainsFailedIntentUntilAnExplicitRetryIsVerified()
    {
        await using Fixture fixture = await Fixture.CreateAsync(("LaunchAtStartupEnabled", "true"));
        fixture.Windows.DenyEnable = true;
        SettingsAuthorityResult failed = await fixture.ApplyAsync(fixture.Startup);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, failed.Status);
        Assert.Equal(SettingAppliedStateKind.Unknown, failed.Envelope!.Applied[SettingsRegistry.Keys.LaunchAtStartupEnabled].Kind);
        SettingsApplicationBatch batch = Assert.Single(failed.Envelope.PendingApplications);
        Assert.Equal(SettingsApplicationBatchState.Failed, batch.State);
        Assert.True((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.Desired[SettingsRegistry.Keys.LaunchAtStartupEnabled].Value.Get<bool>());

        fixture.Windows.DenyEnable = false;
        using (MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary())
        {
            Assert.True((await fixture.Session.RetryAdmittedAsync(batch.BatchId, batch.AttemptId, Guid.NewGuid(), lease, CancellationToken.None)).IsSucceeded);
        }

        Assert.True((await fixture.ApplyAsync(fixture.Startup)).IsSucceeded);
        Assert.Equal(2, fixture.Windows.Mutations);
        Assert.Equal(StartupLaunchTaskState.Enabled, fixture.Windows.State);
    }

    [Fact]
    public async Task StartupUnknown_StopsBeforeAttemptingToChangeWindows()
    {
        await using Fixture fixture = await Fixture.CreateAsync(("LaunchAtStartupEnabled", "true"));
        fixture.Windows.State = StartupLaunchTaskState.Other;
        SettingsAuthorityResult result = await fixture.ApplyAsync(fixture.Startup);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal("settings.application.probe_failed", result.Code);
        Assert.Equal(0, fixture.Windows.Mutations);
    }

    [Fact]
    public async Task StartupReplyLost_IsResolvedByASeparatePlatformObservation()
    {
        await using Fixture fixture = await Fixture.CreateAsync(("LaunchAtStartupEnabled", "true"));
        fixture.Windows.LoseEnableReply = true;
        SettingsAuthorityResult result = await fixture.ApplyAsync(fixture.Startup);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal("settings.application.reply_lost_resolved", result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal(1, fixture.Windows.Mutations);
        Assert.Equal(SettingAppliedValueSource.RuntimeProbe,
            result.Envelope.Applied[SettingsRegistry.Keys.LaunchAtStartupEnabled].Source);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupFatalExceptionGraph_EscapesWithoutClaimingAnOrdinaryFailure(bool duringApply)
    {
        await using Fixture fixture = await Fixture.CreateAsync(("LaunchAtStartupEnabled", "true"));
        InvalidOperationException fatal = new("Platform wrapper.", new AggregateException(Activator.CreateInstance<OutOfMemoryException>()));
        if (duringApply) { fixture.Windows.EnableFailure = fatal; }
        else { fixture.Windows.ReadFailure = fatal; }
        Exception observed = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.ApplyAsync(fixture.Startup));
        Assert.Same(fatal, observed);
        SettingsEnvelope durable = (await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!;
        Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single(durable.PendingApplications).State);
        Assert.Equal(SettingAppliedStateKind.Unknown, durable.Applied[SettingsRegistry.Keys.LaunchAtStartupEnabled].Kind);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignGenerationOrAdmission_IsRejectedBeforeRuntimeAccess(bool sampling)
    {
        await using Fixture fixture = await Fixture.CreateAsync(sampling
            ? [("ConnectionSamplingEnabled", "true"), ("ConnectionSamplingIntervalSeconds", "60")]
            : [("LaunchAtStartupEnabled", "true")]);
        ISettingsApplicationParticipant participant = sampling ? fixture.Sampling : fixture.Startup;
        Capture capture = new(participant);
        Assert.True((await fixture.ApplyAsync(capture)).IsSucceeded);
        SettingsApplicationRequest request = Assert.IsType<SettingsApplicationRequest>(capture.Request);
        int reads = fixture.Windows.Reads;
        int mutations = fixture.Windows.Mutations;
        using MutationAdmissionLease foreign = new MutationAdmissionBarrier().AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.ProbeAsync(request, foreign, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => participant.ApplyAsync(request, foreign, CancellationToken.None));

        ISettingsApplicationParticipant misbound = sampling
            ? new SamplingSettingsParticipant(fixture.Directory.CreateGeneration(2), fixture.Admission, fixture.Loop)
            : new StartupTaskSettingsParticipant(fixture.Directory.CreateGeneration(2), fixture.Admission, fixture.StartupService);
        using MutationAdmissionLease own = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => misbound.ProbeAsync(request, own, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => misbound.ApplyAsync(request, own, CancellationToken.None));
        Assert.Equal(reads, fixture.Windows.Reads);
        Assert.Equal(mutations, fixture.Windows.Mutations);
        Assert.Equal(sampling, fixture.Loop.IsRunning);
    }

    [Fact]
    public async Task SamplingPair_ControlsTheActualLoopWithoutReadingLegacyPreferences()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            ("ConnectionSamplingEnabled", "true"), ("ConnectionSamplingIntervalSeconds", "60"));
        SettingsAuthorityResult result = await fixture.ApplyAsync(fixture.Sampling);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal(new(true, 60), await fixture.Loop.ReadConfigurationAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(60), (await fixture.Clock.ReadAsync()).Duration);
        Assert.True(result.Envelope.Applied[SettingsRegistry.Keys.ConnectionSamplingEnabled].Value!.Get<bool>());
        Assert.Equal(60, result.Envelope.Applied[SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds].Value!.Get<int>());
    }

    [Fact]
    public async Task SamplingDisableThenSingleKeyEnable_UsesTheCompanionIntervalAndRemainsRestartable()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            ("ConnectionSamplingEnabled", "false"), ("ConnectionSamplingIntervalSeconds", "90"));
        await fixture.Loop.ApplyConfigurationAsync(new(true, 30), CancellationToken.None);
        _ = await fixture.Clock.ReadAsync();
        Assert.True((await fixture.ApplyAsync(fixture.Sampling)).IsSucceeded);
        Assert.Equal(new(false, 90), await fixture.Loop.ReadConfigurationAsync(CancellationToken.None));
        using (MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary())
        {
            SettingsAuthorityResult desired = await fixture.Session.ChangeAdmittedAsync(
                [new(SettingsRegistry.Keys.ConnectionSamplingEnabled, SettingsEnvelopeTestData.Value("ConnectionSamplingEnabled", "true"))],
                Guid.NewGuid(), lease, CancellationToken.None);
            Assert.True(desired.IsSucceeded);
            Assert.Single(Assert.Single(desired.Envelope!.PendingApplications).Entries);
        }

        Assert.True((await fixture.ApplyAsync(fixture.Sampling)).IsSucceeded);
        Assert.Equal(TimeSpan.FromSeconds(90), (await fixture.Clock.ReadAsync()).Duration);
        var prior = await fixture.Loop.QuiesceAsync(CancellationToken.None);
        await fixture.Loop.ResumeAsync(prior, CancellationToken.None);
        Assert.Equal(new(true, 90), await fixture.Loop.ReadConfigurationAsync(CancellationToken.None));
        Assert.Equal(TimeSpan.FromSeconds(90), (await fixture.Clock.ReadAsync()).Duration);
    }

    [Fact]
    public async Task SamplingInFlight_OwnsDrainAndFinalPublicationDespiteCancellationAndAdmissionClosure()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            ("ConnectionSamplingEnabled", "false"), ("ConnectionSamplingIntervalSeconds", "90"));
        using CancellationTokenSource caller = new();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        fixture.Source.Block = true;
        await fixture.Loop.ApplyConfigurationAsync(new(true, 30), CancellationToken.None);
        (await fixture.Clock.ReadAsync()).Complete();
        Task<SettingsAuthorityResult>? apply = null;
        Task<MutationAdmissionLease>? drain = null;
        try
        {
            await fixture.Source.Entered.Task.WaitAsync(deadline.Token);
            apply = fixture.ApplyAsync(fixture.Sampling, caller.Token);
            await fixture.Source.CancellationObserved.Task.WaitAsync(deadline.Token);
            caller.Cancel();
            drain = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, deadline.Token).AsTask();
            Assert.False(apply.IsCompleted);
            Assert.False(drain.IsCompleted);
            SettingsEnvelope during = (await fixture.Repository.OpenAsync(deadline.Token)).Envelope!;
            Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single(during.PendingApplications).State);
            Assert.Equal(SettingAppliedStateKind.Unknown, during.Applied[SettingsRegistry.Keys.ConnectionSamplingEnabled].Kind);
        }
        finally
        {
            fixture.Source.Release.TrySetResult();
            try { if (apply is not null) { await apply; } }
            finally { if (drain is not null) { await using MutationAdmissionLease lease = await drain; } }
        }

        Assert.True((await apply!).IsSucceeded);
        Assert.Equal(new(false, 90), await fixture.Loop.ReadConfigurationAsync(CancellationToken.None));
        Assert.Empty((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications);
    }

    [Fact]
    public async Task SamplingTerminalStop_CannotTurnPersistedEnableIntentIntoFalseSuccess()
    {
        await using Fixture fixture = await Fixture.CreateAsync(
            ("ConnectionSamplingEnabled", "true"), ("ConnectionSamplingIntervalSeconds", "60"));
        await fixture.Loop.StopAsync(CancellationToken.None);
        SettingsAuthorityResult result = await fixture.ApplyAsync(fixture.Sampling);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal("settings.application.verification_failed", result.Code);
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(result.Envelope!.PendingApplications).State);
        Assert.Equal(SettingAppliedStateKind.Unknown, result.Envelope.Applied[SettingsRegistry.Keys.ConnectionSamplingEnabled].Kind);
        Assert.False(fixture.Loop.IsRunning);
    }

    [Fact]
    public async Task DesiredInterval_RemainsSeparateFromTheScheduleUntilApplicationCompletes()
    {
        await using Fixture fixture = await Fixture.CreateAsync(("ConnectionSamplingIntervalSeconds", "120"));
        await fixture.Loop.ApplyConfigurationAsync(new(true, 30), CancellationToken.None);
        Delay original = await fixture.Clock.ReadAsync();
        original.Complete();
        Assert.Equal(TimeSpan.FromSeconds(30), (await fixture.Clock.ReadAsync()).Duration);
        Assert.Equal(new(true, 30), await fixture.Loop.ReadConfigurationAsync(CancellationToken.None));
        Assert.Equal(120, (await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!
            .Desired[SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds].Value.Get<int>());
        Assert.True((await fixture.ApplyAsync(fixture.Sampling)).IsSucceeded);
        Assert.Equal(TimeSpan.FromSeconds(120), (await fixture.Clock.ReadAsync()).Duration);
    }

    [Fact]
    public async Task LegacyPreferenceChange_DoesNotReplaceAnActiveScheduleUntilTheOwnedRestart()
    {
        MutablePreferences preferences = new();
        Clock clock = new();
        ConnectionSamplingService loop = new(preferences, new Source(), new Storage(), key => key, clock);
        try
        {
            await loop.StartAsync(CancellationToken.None);
            Delay original = await clock.ReadAsync();
            Assert.Equal(TimeSpan.FromSeconds(30), original.Duration);
            preferences.IntervalSeconds = 120;
            original.Complete();
            Assert.Equal(TimeSpan.FromSeconds(30), (await clock.ReadAsync()).Duration);
            await loop.RestartFromSettingsAsync(CancellationToken.None);
            Assert.Equal(TimeSpan.FromSeconds(120), (await clock.ReadAsync()).Duration);
        }
        finally { await loop.StopAsync(CancellationToken.None); }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public DataGenerationTestDirectory Directory { get; } = new();
        public MutationAdmissionBarrier Admission { get; } = new();
        public WindowsTask Windows { get; } = new();
        public Clock Clock { get; } = new();
        public Source Source { get; } = new();
        public JsonSettingsRepository Repository { get; private set; } = null!;
        public SettingsAuthoritySession Session { get; private set; } = null!;
        public ConnectionSamplingService Loop { get; private set; } = null!;
        public StartupLaunchService StartupService { get; private set; } = null!;
        public StartupTaskSettingsParticipant Startup { get; private set; } = null!;
        public SamplingSettingsParticipant Sampling { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync(params (string Key, string Value)[] changes)
        {
            Fixture fixture = new();
            try
            {
                var manifest = await fixture.Directory.PromoteFirstAsync();
                fixture.Repository = new(manifest.Descriptor, SettingsRegistry.Default);
                Assert.True((await fixture.Repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
                SettingsEnvelope pending = SettingsEnvelopeTestData.CreatePendingEnvelope(changes);
                Dictionary<SettingKey, SettingAppliedState> applied = pending.Applied.ToDictionary();
                foreach (SettingsApplicationBatchEntry entry in pending.PendingApplications.SelectMany(batch => batch.Entries))
                {
                    applied[entry.Key] = SettingAppliedState.Unknown(SettingAppliedUnknownReason.NotObserved, SettingAppliedUnknownHandling.QueueApplication);
                }

                SettingsPersistenceResult seed = await fixture.Repository.SaveAsync(new(pending.SchemaVersion, pending.EnvelopeRevision,
                    pending.Desired, applied, pending.PendingApplications, pending.MigrationHistory), 1, CancellationToken.None);
                Assert.True(seed.IsSucceeded, seed.Diagnostic?.Code);
                fixture.Session = new(fixture.Repository, SettingsRegistry.Default, fixture.Admission);
                fixture.Loop = new(new PoisonPreferences(), fixture.Source, new Storage(), key => key, fixture.Clock);
                fixture.StartupService = new(fixture.Windows, new Storage(), key => key);
                fixture.Startup = new(manifest.Descriptor, fixture.Admission, fixture.StartupService);
                fixture.Sampling = new(manifest.Descriptor, fixture.Admission, fixture.Loop);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public async Task<SettingsAuthorityResult> ApplyAsync(ISettingsApplicationParticipant participant, CancellationToken cancellationToken = default)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary(cancellationToken);
            SettingsApplicationBatch batch = Assert.Single((await Repository.OpenAsync(cancellationToken)).Envelope!.PendingApplications);
            return await Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId, participant, SettingsApplicationPhase.Live, lease, cancellationToken);
        }

        public async ValueTask DisposeAsync()
        {
            Source.Release.TrySetResult();
            if (Loop is not null) { await Loop.StopAsync(CancellationToken.None); }
            if (Session is not null) { await Session.DisposeAsync(); }
            await Directory.DisposeAsync();
        }
    }

    private sealed class WindowsTask : IStartupLaunchTaskProvider, IStartupLaunchTask
    {
        public StartupLaunchTaskState State { get; set; }
        public bool DenyEnable { get; set; }
        public bool LoseEnableReply { get; set; }
        public Exception? ReadFailure { get; set; }
        public Exception? EnableFailure { get; set; }
        public int Reads { get; private set; }
        public int Mutations { get; private set; }
        public Task<IStartupLaunchTask> GetAsync(string taskId)
        {
            Assert.Equal(StartupLaunchService.TaskId, taskId);
            ++Reads;
            if (ReadFailure is not null) { throw ReadFailure; }
            return Task.FromResult<IStartupLaunchTask>(this);
        }

        public Task<StartupLaunchTaskState> RequestEnableAsync()
        {
            ++Mutations;
            if (EnableFailure is not null) { throw EnableFailure; }
            if (!DenyEnable) { State = StartupLaunchTaskState.Enabled; }
            if (LoseEnableReply) { throw new InvalidOperationException("Lost platform reply."); }
            return Task.FromResult(State);
        }

        public void Disable() { ++Mutations; State = StartupLaunchTaskState.Disabled; }
    }

    private sealed class PoisonPreferences : IConnectionSamplingSettings
    {
        public bool IsEnabled => throw new InvalidOperationException("Legacy preferences must not be read.");
        public int IntervalSeconds => throw new InvalidOperationException("Legacy preferences must not be read.");
    }

    private sealed class MutablePreferences : IConnectionSamplingSettings
    {
        public bool IsEnabled => true;
        public int IntervalSeconds { get; set; } = 30;
    }

    private sealed class Storage : IConnectionSamplingStorage, IStartupLaunchLog
    {
        public int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections) => 0;
        public void AppendLog(string level, string category, string message, string? detail) { }
    }

    private sealed class Source : IConnectionSamplingSource
    {
        public bool Block { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => CancellationObserved.TrySetResult());
            Entered.TrySetResult();
            if (Block) { await Release.Task; }
            return [];
        }
    }

    private sealed class Clock : ISupervisorClock
    {
        private readonly Channel<Delay> _delays = Channel.CreateUnbounded<Delay>();
        public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch;
        public async Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
        {
            Delay item = new(delay);
            Assert.True(_delays.Writer.TryWrite(item));
            await item.WaitAsync(cancellationToken);
        }

        public async Task<Delay> ReadAsync()
        {
            using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(10));
            return await _delays.Reader.ReadAsync(deadline.Token);
        }
    }

    private sealed class Delay(TimeSpan duration)
    {
        private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TimeSpan Duration { get; } = duration;
        public void Complete() => _completed.TrySetResult();
        public async Task WaitAsync(CancellationToken cancellationToken)
        {
            using CancellationTokenRegistration registration = cancellationToken.Register(() => _completed.TrySetCanceled(cancellationToken));
            await _completed.Task;
        }
    }

    private sealed class Capture(ISettingsApplicationParticipant participant) : ISettingsApplicationParticipant
    {
        public SettingsApplicationRequest? Request { get; private set; }
        public SettingApplicationKind ApplicationKind => participant.ApplicationKind;
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            Request = request;
            return participant.ProbeAsync(request, admissionLease, cancellationToken);
        }

        public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
            participant.ApplyAsync(request, admissionLease, cancellationToken);
    }
}
