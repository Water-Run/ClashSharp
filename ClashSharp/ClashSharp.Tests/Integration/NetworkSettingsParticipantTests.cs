extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;
using NetworkSettingsConfiguration = ClashSharpUi::ClashSharp.Hosting.Settings.NetworkSettingsConfiguration;
using NetworkSettingsParticipant = ClashSharpUi::ClashSharp.Hosting.Settings.NetworkSettingsParticipant;
using NetworkSettingsRuntime = ClashSharpUi::ClashSharp.Hosting.Settings.NetworkSettingsRuntime;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises durable settings against independently observed, isolated network boundaries.</summary>
public sealed class NetworkSettingsParticipantTests
{
    [Fact]
    public async Task CompleteBatch_PersistsRunningBeforeApplyingAndVerifiesAllFourKeys()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("CurrentMode", "RuleTakeover"), Change("ActiveProfileId", "test-profile"),
            Change("TransparentProxyEnabled", "false"), Change("MixedPort", "18080"))).IsSucceeded);
        fixture.Surface.BeforeApply = async () =>
        {
            Assert.Equal(SettingsApplicationBatchState.Running,
                Assert.Single((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications).State);
        };
        SettingsAuthorityResult result = await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal(new NetworkSettingsConfiguration(ClashSharpMode.RuleTakeover, "test-profile", false, 18080), fixture.Surface.Actual);
        Assert.Equal(1, fixture.Surface.Applies);
        Assert.True(fixture.Surface.Reads >= 4);
    }

    [Fact]
    public async Task SingleKey_DoesNotConsumeOtherPendingNetworkIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("ActiveProfileId", "pending-profile"))).IsSucceeded);
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18081"))).IsSucceeded);
        SettingsAuthorityResult result = await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal("builtin-direct", fixture.Surface.Actual.ProfileId);
        Assert.Equal(18081, fixture.Surface.Actual.MixedPort);
        Assert.Equal(SettingsRegistry.Keys.ActiveProfileId, Assert.Single(Assert.Single(result.Envelope!.PendingApplications).Entries).Key);
    }

    [Theory]
    [InlineData(ClashSharpMode.Disabled)]
    [InlineData(ClashSharpMode.Standby)]
    public async Task InactiveTunPreference_IsInstalledWithoutClaimingEffectiveTun(ClashSharpMode mode)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        // Force a pending retry even though the registry's initial Desired value is true.
        Assert.True((await fixture.ChangeAsync(Change("TransparentProxyEnabled", "false"))).IsSucceeded);
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.TransparentProxyEnabled)).IsSucceeded);
        Assert.True((await fixture.ChangeAsync(Change("TransparentProxyEnabled", "true"), Change("CurrentMode", mode.ToString()))).IsSucceeded);
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.TransparentProxyEnabled)).IsSucceeded);
        NetworkSettingsConfiguration installed = await fixture.Runtime.ReadConfigurationAsync(CancellationToken.None);
        Assert.True(installed.TransparentProxyEnabled);
        Assert.False(installed.EffectiveTunEnabled);
        Assert.False(fixture.Surface.Actual.TransparentProxyEnabled);
    }

    [Fact]
    public async Task NativeLostReply_UsesIndependentObservationToComplete()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Surface.LoseReply = true;
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18082"))).IsSucceeded);
        SettingsAuthorityResult result = await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal(18082, fixture.Surface.Actual.MixedPort);
        Assert.Equal(1, fixture.Surface.Applies);
    }

    [Fact]
    public async Task UnknownBaseline_BlocksEveryEffectAndRetainsFailedDesired()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Surface.Unavailable = true;
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18083"))).IsSucceeded);
        SettingsAuthorityResult result = await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(result.Envelope!.PendingApplications).State);
        Assert.Equal(18083, result.Envelope.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>());
        Assert.Equal(0, fixture.Surface.Applies);
    }

    [Fact]
    public async Task FailedCompoundEffect_CannotAcknowledgeOneMatchingScalarOrBlindlyRetry()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Surface.PartialProfileFailure = true;
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18084"))).IsSucceeded);
        SettingsAuthorityResult result = await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal(18084, fixture.Surface.Actual.MixedPort);
        Assert.Equal("unintended-profile", fixture.Surface.Actual.ProfileId);
        SettingsApplicationRequest staleAttempt = fixture.Capture.Request!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.ReadConfigurationAsync(CancellationToken.None));
        using MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ApplyAsync(fixture.Capture.Request!, lease, CancellationToken.None));
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed,
            (await fixture.RetryAsync(SettingsRegistry.Keys.MixedPort)).Status);
        int readsAfterRetry = fixture.Surface.Reads;
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ProbeAsync(staleAttempt, lease, CancellationToken.None));
        Assert.Equal(readsAfterRetry, fixture.Surface.Reads);
        Assert.Equal(1, fixture.Surface.Applies);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitRetry_UnlocksOnlyAfterCompleteBaselineOrTargetRecovers(bool targetRecovered)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Surface.BeforeApply = () => { fixture.Surface.Unavailable = true; return Task.CompletedTask; };
        fixture.Surface.LoseReply = targetRecovered;
        fixture.Surface.Failure = targetRecovered ? null : new IOException("isolated apply and compensation reply failure");
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18088"))).IsSucceeded);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, (await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort)).Status);
        fixture.Surface.Unavailable = false;
        fixture.Surface.Failure = null;
        fixture.Surface.LoseReply = false;
        fixture.Surface.BeforeApply = null;
        // Availability alone does not make a final probe or stale attempt safe.
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Runtime.ReadConfigurationAsync(CancellationToken.None));
        int readsBeforeRetry = fixture.Surface.Reads;
        SettingsAuthorityResult result = await fixture.RetryAsync(SettingsRegistry.Keys.MixedPort);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal(18088, fixture.Surface.Actual.MixedPort);
        Assert.Equal(targetRecovered ? 1 : 2, fixture.Surface.Applies);
        Assert.True(fixture.Surface.Reads > readsBeforeRetry);
    }

    [Fact]
    public async Task ExplicitRetry_WhenInactiveTunPolicyHasNoExternalEvidence_ReinstallsBeforePublishing()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("TransparentProxyEnabled", "false"))).IsSucceeded);
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.TransparentProxyEnabled)).IsSucceeded);
        fixture.Surface.BeforeApply = () => { fixture.Surface.Unavailable = true; return Task.CompletedTask; };
        fixture.Surface.LoseReply = true;
        Assert.True((await fixture.ChangeAsync(Change("TransparentProxyEnabled", "true"))).IsSucceeded);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed,
            (await fixture.ApplyAsync(SettingsRegistry.Keys.TransparentProxyEnabled)).Status);
        fixture.Surface.Unavailable = false;
        fixture.Surface.LoseReply = false;
        fixture.Surface.BeforeApply = null;
        int applies = fixture.Surface.Applies;
        Assert.True((await fixture.RetryAsync(SettingsRegistry.Keys.TransparentProxyEnabled)).IsSucceeded);
        Assert.Equal(applies + 1, fixture.Surface.Applies);
        Assert.True((await fixture.Runtime.ReadConfigurationAsync(CancellationToken.None)).TransparentProxyEnabled);
        Assert.False(fixture.Surface.Actual.TransparentProxyEnabled);
    }

    [Fact]
    public async Task SupersedingFailedBatch_RequiresExplicitRetryBeforeRecoveringAndApplyingNewIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Surface.BeforeApply = () => { fixture.Surface.Unavailable = true; return Task.CompletedTask; };
        fixture.Surface.Failure = new IOException("isolated runtime compensation reply unavailable");
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18089"))).IsSucceeded);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, (await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort)).Status);
        Guid originalBatch = fixture.Capture.Request!.Batch.BatchId;
        fixture.Surface.Unavailable = false;
        fixture.Surface.Failure = null;
        fixture.Surface.BeforeApply = null;
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18090"))).IsSucceeded);
        int reads = fixture.Surface.Reads;
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, (await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort)).Status);
        Assert.NotEqual(originalBatch, fixture.Capture.Request!.Batch.BatchId);
        Assert.Equal(reads, fixture.Surface.Reads);
        Assert.Equal(1, fixture.Surface.Applies);
        SettingsAuthorityResult retry = await fixture.RetryAsync(SettingsRegistry.Keys.MixedPort);
        Assert.True(retry.IsSucceeded, retry.Code);
        Assert.Empty(retry.Envelope!.PendingApplications);
        Assert.Equal(18090, fixture.Surface.Actual.MixedPort);
        Assert.Equal(2, fixture.Surface.Applies);
    }

    [Fact]
    public async Task FatalNativeGraph_EscapesUnchangedAndLeavesRunningIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Exception fatal = new AggregateException(new InvalidOperationException("fatal wrapper", Activator.CreateInstance<OutOfMemoryException>()));
        fixture.Surface.Failure = fatal;
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18085"))).IsSucceeded);
        Assert.Same(fatal, await Assert.ThrowsAsync<AggregateException>(() => fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort)));
        Assert.Equal(SettingsApplicationBatchState.Running,
            Assert.Single((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications).State);
        Assert.Equal(10000, fixture.Surface.Actual.MixedPort);
    }

    [Fact]
    public async Task ForeignGenerationAndInactiveLease_AreRejectedBeforeRuntimeReads()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18086"))).IsSucceeded);
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort)).IsSucceeded);
        int reads = fixture.Surface.Reads;
        await using NetworkSettingsParticipant foreign = new(fixture.Directory.CreateGeneration(2), fixture.Admission, fixture.Runtime);
        using MutationAdmissionLease own = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => foreign.ProbeAsync(fixture.Capture.Request!, own, CancellationToken.None));
        using MutationAdmissionLease other = new MutationAdmissionBarrier().AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ApplyAsync(fixture.Capture.Request!, other, CancellationToken.None));
        MutationAdmissionLease released = fixture.Admission.AcquireOrdinary();
        released.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ProbeAsync(fixture.Capture.Request!, released, CancellationToken.None));
        Assert.Equal(reads, fixture.Surface.Reads);
    }

    [Fact]
    public async Task Retirement_DrainsStartedNativeTransactionAndRejectsNewCalls()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("MixedPort", "18087"))).IsSucceeded);
        TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Surface.BeforeApply = async () => { started.TrySetResult(); await release.Task; };
        Task<SettingsAuthorityResult> operation = fixture.ApplyAsync(SettingsRegistry.Keys.MixedPort);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task retirement = fixture.Participant.DisposeAsync().AsTask();
        try
        {
            Assert.False(retirement.IsCompleted);
            using MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Participant.ProbeAsync(fixture.Capture.Request!, lease, CancellationToken.None));
        }
        finally { release.TrySetResult(); }
        await retirement;
        // Direct retirement before the session's final probe intentionally leaves the
        // durable attempt failed, while the already-started native transaction is drained.
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, (await operation).Status);
        Assert.Equal(18087, fixture.Surface.Actual.MixedPort);
    }

    private static SettingValueChange Change(string key, string value) => new(new(key), SettingsEnvelopeTestData.Value(key, value));

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture()
        {
            DataGenerationDescriptor generation = Directory.CreateGeneration(1);
            Repository = new(generation, SettingsRegistry.Default);
            Session = new(Repository, SettingsRegistry.Default, Admission);
            Runtime = new(Surface.ObserveAsync, Surface.ApplyAsync);
            Participant = new(generation, Admission, Runtime);
            Capture = new(Participant);
        }
        public DataGenerationTestDirectory Directory { get; } = new();
        public MutationAdmissionBarrier Admission { get; } = new();
        public Surface Surface { get; } = new();
        public JsonSettingsRepository Repository { get; }
        public SettingsAuthoritySession Session { get; }
        public NetworkSettingsRuntime Runtime { get; }
        public NetworkSettingsParticipant Participant { get; }
        public CaptureParticipant Capture { get; }

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            Assert.True((await fixture.Repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
            return fixture;
        }

        public async Task<SettingsAuthorityResult> ChangeAsync(params SettingValueChange[] changes)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            return await Session.ChangeAdmittedAsync(changes, Guid.NewGuid(), lease, CancellationToken.None);
        }

        public async Task<SettingsAuthorityResult> ApplyAsync(SettingKey key)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            SettingsApplicationBatch batch = Assert.Single((await Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications,
                candidate => candidate.Entries.Any(entry => entry.Key == key));
            return await Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId, Capture, SettingsApplicationPhase.Live, lease, CancellationToken.None);
        }

        public async Task<SettingsAuthorityResult> RetryAsync(SettingKey key)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            SettingsApplicationBatch batch = Assert.Single((await Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications,
                candidate => candidate.Entries.Any(entry => entry.Key == key));
            Guid retry = Guid.NewGuid();
            Assert.True((await Session.RetryAdmittedAsync(batch.BatchId, batch.AttemptId, retry, lease, CancellationToken.None)).IsSucceeded);
            return await Session.ApplyBatchAdmittedAsync(batch.BatchId, retry, Capture, SettingsApplicationPhase.Live, lease, CancellationToken.None);
        }

        public async ValueTask DisposeAsync() { await Session.DisposeAsync(); await Participant.DisposeAsync(); await Directory.DisposeAsync(); }
    }

    private sealed class CaptureParticipant(NetworkSettingsParticipant participant) : ISettingsApplicationParticipant
    {
        public SettingApplicationKind ApplicationKind => SettingApplicationKind.Network;
        public SettingsApplicationRequest? Request { get; private set; }
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken token)
        { Request = request; return participant.ProbeAsync(request, lease, token); }
        public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken token) => participant.ApplyAsync(request, lease, token);
    }

    private sealed class Surface
    {
        public NetworkSettingsConfiguration Actual { get; private set; } = new(ClashSharpMode.Disabled, "builtin-direct", false, 10000);
        public int Reads { get; private set; }
        public int Applies { get; private set; }
        public bool Unavailable { get; set; }
        public bool LoseReply { get; set; }
        public bool PartialProfileFailure { get; set; }
        public Exception? Failure { get; set; }
        public Func<Task>? BeforeApply { get; set; }
        public Task<NetworkSettingsConfiguration> ObserveAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            ++Reads;
            return Unavailable ? Task.FromException<NetworkSettingsConfiguration>(new IOException("Isolated unavailable network observation.")) : Task.FromResult(Actual);
        }
        public async Task ApplyAsync(NetworkSettingsConfiguration target, CancellationToken token)
        {
            Assert.False(token.CanBeCanceled);
            ++Applies;
            if (BeforeApply is not null) { await BeforeApply(); }
            if (Failure is not null) { throw Failure; }
            Actual = new(target.Mode, PartialProfileFailure ? "unintended-profile" : target.ProfileId, target.EffectiveTunEnabled, target.MixedPort);
            if (LoseReply || PartialProfileFailure) { throw new IOException("Isolated native reply failure."); }
        }
    }
}
