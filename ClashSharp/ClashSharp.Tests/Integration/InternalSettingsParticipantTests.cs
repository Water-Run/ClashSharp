using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises actual internal consumer snapshots with durable settings and generation lifetimes.</summary>
public sealed class InternalSettingsParticipantTests
{
    [Fact]
    public async Task Defaults_ExposeOnlyInternalConsumerKeysWithoutOpeningStorage()
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        string[] entriesBefore = Directory.GetFileSystemEntries(directory.RootPath, "*", SearchOption.AllDirectories);
        using InternalSettingsParticipant participant = new(generation, new(), SettingsRegistry.Default);
        InternalSettingsSnapshot snapshot = participant.CaptureSnapshot();
        Assert.Same(generation, snapshot.Generation);
        Assert.Equal(SettingsRegistry.Default.Definitions.Where(definition => definition.ApplicationKind == SettingApplicationKind.Internal)
            .Select(definition => definition.Key).OrderBy(key => key.Value), snapshot.Values.Keys.OrderBy(key => key.Value));
        Assert.True(snapshot.Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Throws<KeyNotFoundException>(() => snapshot.Get<bool>(SettingsRegistry.Keys.TriggersEnabled));
        Assert.Throws<InvalidOperationException>(() => snapshot.Get<string>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Throws<NotSupportedException>(() => ((IDictionary<SettingKey, SettingValue>)snapshot.Values).Clear());
        Assert.Equal(entriesBefore, Directory.GetFileSystemEntries(directory.RootPath, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DurableDesired_DoesNotChangeConsumersUntilTheCompleteBatchIsApplied()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        InternalSettingsSnapshot original = fixture.Current();
        SettingsAuthorityResult changed = await fixture.ChangeAsync(
            Change("NotificationEnabled", "false"), Change("ConnectionTestProxyUrl1", "https://one.example/"),
            Change("ConnectionTestProxyUrl2", "https://two.example/"), Change("ConnectionTestDirectUrl", "https://three.example/"));
        Assert.True(changed.IsSucceeded, changed.Code);
        Assert.Same(original, fixture.Current());
        CapturingParticipant wrapper = new(fixture.Participant)
        {
            BeforeApply = async () =>
            {
                Assert.Same(original, fixture.Current());
                Assert.Equal(SettingsApplicationBatchState.Running,
                    Assert.Single((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications).State);
            },
        };
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.NotificationEnabled, wrapper)).IsSucceeded);
        InternalSettingsSnapshot installed = fixture.Current();
        Assert.NotSame(original, installed);
        Assert.False(installed.Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Equal("https://one.example", installed.Get<string>(SettingsRegistry.Keys.ConnectionTestProxyUrl1));
        Assert.Equal("https://two.example", installed.Get<string>(SettingsRegistry.Keys.ConnectionTestProxyUrl2));
        Assert.Equal("https://three.example", installed.Get<string>(SettingsRegistry.Keys.ConnectionTestDirectUrl));
        Assert.True(original.Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Equal(SettingsRegistry.Default.Get("ConnectionTestProxyUrl1").DefaultValue,
            original.Values[SettingsRegistry.Keys.ConnectionTestProxyUrl1]);
        Assert.Equal(1, wrapper.Applies);
    }

    [Fact]
    public async Task SingleKeyApplication_PreservesOtherPendingInternalIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("ConnectionTestProxyUrl1", "https://pending.example/"))).IsSucceeded);
        SettingsAuthorityResult result = await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")],
            Guid.NewGuid(), CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.False(fixture.Current().Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Equal(SettingsRegistry.Default.Get("ConnectionTestProxyUrl1").DefaultValue,
            fixture.Current().Values[SettingsRegistry.Keys.ConnectionTestProxyUrl1]);
        Assert.Equal(SettingsRegistry.Keys.ConnectionTestProxyUrl1, Assert.Single(Assert.Single(result.Envelope!.PendingApplications).Entries).Key);
        Assert.True((await fixture.ApplyAsync(SettingsRegistry.Keys.ConnectionTestProxyUrl1)).IsSucceeded);
        Assert.Equal("https://pending.example", fixture.Current().Get<string>(SettingsRegistry.Keys.ConnectionTestProxyUrl1));
        Assert.False(fixture.Current().Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
    }

    [Fact]
    public async Task LostApplyReply_IsResolvedFromTheActualConsumerSnapshot()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.ChangeAsync(Change("NotificationEnabled", "false"))).IsSucceeded);
        CapturingParticipant wrapper = new(fixture.Participant) { LoseReply = true };
        SettingsAuthorityResult result = await fixture.ApplyAsync(SettingsRegistry.Keys.NotificationEnabled, wrapper);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Equal("settings.application.reply_lost_resolved", result.Code);
        Assert.False(fixture.Current().Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Equal(1, wrapper.Applies);
        Assert.Empty(result.Envelope!.PendingApplications);
    }

    [Fact]
    public async Task ForeignGenerationAndInactiveAdmission_CannotChangeInstalledValues()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CapturingParticipant capture = await fixture.CaptureAttemptAsync();
        using InternalSettingsParticipant other = new(fixture.Directory.CreateGeneration(2), fixture.Admission, SettingsRegistry.Default);
        InternalSettingsSnapshot original = other.CaptureSnapshot();
        using MutationAdmissionLease own = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ApplyAsync(capture.Request!, own, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => other.ProbeAsync(capture.Request!, own, CancellationToken.None));
        using MutationAdmissionLease foreign = new MutationAdmissionBarrier().AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ApplyAsync(capture.Request!, foreign, CancellationToken.None));
        MutationAdmissionLease retired = fixture.Admission.AcquireOrdinary();
        retired.Dispose();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ProbeAsync(capture.Request!, retired, CancellationToken.None));
        Assert.Same(original, other.CaptureSnapshot());
    }

    [Fact]
    public async Task AnotherApplicationKind_IsRejectedBeforePublication()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        InternalSettingsSnapshot original = fixture.Current();
        Assert.True((await fixture.ChangeAsync(Change("LaunchAtStartupEnabled", "true"))).IsSucceeded);
        CapturingParticipant wrongKind = new(fixture.Participant) { ApplicationKind = SettingApplicationKind.StartupTask };
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed,
            (await fixture.ApplyAsync(SettingsRegistry.Keys.LaunchAtStartupEnabled, wrongKind)).Status);
        using MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Participant.ApplyAsync(wrongKind.Request!, lease, CancellationToken.None));
        Assert.Same(original, fixture.Current());
    }

    [Fact]
    public async Task CancelledDirectCalls_LeaveTheInstalledSnapshotUntouched()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CapturingParticipant capture = await fixture.CaptureAttemptAsync();
        InternalSettingsSnapshot original = fixture.Current();
        using MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Participant.ApplyAsync(capture.Request!, lease, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Participant.ProbeAsync(capture.Request!, lease, cancelled.Token));
        Assert.Same(original, fixture.Current());
    }

    [Fact]
    public async Task Retirement_RejectsLiveAccessAndPreservesHistoricalSnapshotsAndStorage()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        CapturingParticipant capture = await fixture.CaptureAttemptAsync();
        InternalSettingsSnapshot historical = fixture.Current();
        SettingsEnvelope before = (await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!;
        fixture.Participant.Dispose();
        fixture.Participant.Dispose();
        Assert.Throws<ObjectDisposedException>(() => fixture.Current());
        using MutationAdmissionLease lease = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Participant.ProbeAsync(capture.Request!, lease, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.Participant.ApplyAsync(capture.Request!, lease, CancellationToken.None));
        Assert.True(historical.Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Equal(SettingsEnvelopeCodec.Encode(before, SettingsRegistry.Default).ContentHash,
            SettingsEnvelopeCodec.Encode((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!, SettingsRegistry.Default).ContentHash);
    }

    [Fact]
    public async Task Startup_ReobservesANewConsumerOwnerBeforeReinstallingDurableIntent()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")],
            Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        fixture.Participant.Dispose();
        using InternalSettingsParticipant reopened = new(fixture.Session.Generation, fixture.Admission, SettingsRegistry.Default);
        Assert.True(reopened.CaptureSnapshot().Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        await using MutationAdmissionLease startup = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        SettingsAuthorityResult prepared = await fixture.Session.PrepareStartupAdmittedAsync(Guid.NewGuid(), startup, CancellationToken.None);
        SettingsApplicationBatch batch = Assert.Single(prepared.Envelope!.PendingApplications, item => item.ApplicationKind == SettingApplicationKind.Internal);
        CapturingParticipant wrapper = new(reopened);
        SettingsAuthorityResult applied = await fixture.Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId,
            wrapper, SettingsApplicationPhase.Startup, startup, CancellationToken.None);
        Assert.True(applied.IsSucceeded, applied.Code);
        Assert.False(reopened.CaptureSnapshot().Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.Equal(SettingAppliedValueSource.StartupReconciliation, applied.Envelope!.Applied[SettingsRegistry.Keys.NotificationEnabled].Source);
        await startup.DisposeAsync();
        using MutationAdmissionLease ordinary = fixture.Admission.AcquireOrdinary();
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.ApplyAsync(wrapper.Request!, ordinary, CancellationToken.None));
    }

    [Fact]
    public async Task GenerationSwap_ResolvesOnlyTheNewInstalledOwnerAndRetiresThePreviousOne()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        Assert.True((await fixture.Authority.ApplyChangesAsync([Change("NotificationEnabled", "false")],
            Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        InternalSettingsSnapshot previous = fixture.Current();
        DataGenerationTransition transition = await fixture.Generations.BeginDrainAsync(fixture.Generations.CurrentManifest.ContentHash, CancellationToken.None);
        DataGenerationDescriptor candidate = fixture.Directory.CreateGeneration(2);
        Lifetime next = await Lifetime.CreateAsync(candidate, fixture.Admission);
        transition.Stage(new(candidate, next));
        await transition.PromoteManifestAsync(fixture.Directory.Store, CancellationToken.None);
        transition.SwapToPromoted();
        await transition.CommitAsync();
        Assert.Throws<ObjectDisposedException>(() => fixture.Participant.CaptureSnapshot());
        Assert.Equal(candidate.GenerationId, fixture.Current().Generation.GenerationId);
        Assert.True(fixture.Current().Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.False(previous.Get<bool>(SettingsRegistry.Keys.NotificationEnabled));
        Assert.True((await fixture.Authority.ApplyChangesAsync([Change("CheckStaleProxyOnStartup", "false")],
            Guid.NewGuid(), CancellationToken.None)).IsSucceeded);
        Assert.False(fixture.Current().Get<bool>(SettingsRegistry.Keys.CheckStaleProxyOnStartup));
        Assert.False((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!.Desired[SettingsRegistry.Keys.NotificationEnabled].Value.Get<bool>());
    }

    private static SettingValueChange Change(string key, string value) => new(new(key), SettingsEnvelopeTestData.Value(key, value));

    private sealed class Fixture : IAsyncDisposable
    {
        public DataGenerationTestDirectory Directory { get; } = new();
        public MutationAdmissionBarrier Admission { get; } = new();
        public DataGenerationManager Generations { get; } = new();
        public Lifetime Lifetime { get; private set; } = null!;
        public InternalSettingsParticipant Participant => Lifetime.Participant;
        public SettingsAuthoritySession Session => Lifetime.Session;
        public JsonSettingsRepository Repository => Lifetime.Repository;
        public GenerationSettingsAuthority Authority { get; private set; } = null!;

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                DataGenerationManifestSnapshot manifest = await fixture.Directory.PromoteFirstAsync();
                fixture.Lifetime = await InternalSettingsParticipantTests.Lifetime.CreateAsync(manifest.Descriptor, fixture.Admission);
                fixture.Generations.Initialize(manifest, new(manifest.Descriptor, fixture.Lifetime));
                fixture.Authority = new(fixture.Generations, fixture.Admission);
                return fixture;
            }
            catch { await fixture.DisposeAsync(); throw; }
        }

        public InternalSettingsSnapshot Current() => Generations.ReadSnapshot<IInternalSettingsReader, InternalSettingsSnapshot>(
            (participant, generation) => { Assert.True(participant.CaptureSnapshot().Generation.IsSameGeneration(generation)); return participant.CaptureSnapshot(); });

        public async Task<SettingsAuthorityResult> ChangeAsync(params SettingValueChange[] changes)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            return await Session.ChangeAdmittedAsync(changes, Guid.NewGuid(), lease, CancellationToken.None);
        }

        public async Task<SettingsAuthorityResult> ApplyAsync(SettingKey key, ISettingsApplicationParticipant? participant = null)
        {
            using MutationAdmissionLease lease = Admission.AcquireOrdinary();
            SettingsApplicationBatch batch = Assert.Single((await Repository.OpenAsync(CancellationToken.None)).Envelope!.PendingApplications,
                item => item.Entries.Any(entry => entry.Key == key));
            return await Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId, participant ?? Participant,
                SettingsApplicationPhase.Live, lease, CancellationToken.None);
        }

        public async Task<CapturingParticipant> CaptureAttemptAsync()
        {
            Assert.True((await ChangeAsync(Change("NotificationEnabled", "false"))).IsSucceeded);
            CapturingParticipant capture = new(Participant) { BeforeApply = () => throw new IOException("Isolated interruption before publication.") };
            Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, (await ApplyAsync(SettingsRegistry.Keys.NotificationEnabled, capture)).Status);
            Assert.NotNull(capture.Request);
            return capture;
        }

        public async ValueTask DisposeAsync()
        {
            await Generations.DisposeAsync();
            if (Lifetime is not null) { await Lifetime.DisposeAsync(); }
            await Directory.DisposeAsync();
        }
    }

    private sealed class Lifetime : IServiceProvider, IAsyncDisposable
    {
        private readonly SettingsGenerationContext _context;
        private Lifetime(DataGenerationDescriptor generation, MutationAdmissionBarrier admission)
        {
            Repository = new(generation, SettingsRegistry.Default);
            Session = new(Repository, SettingsRegistry.Default, admission);
            Participant = new(generation, admission, SettingsRegistry.Default);
            _context = new(Session, [Participant]);
        }
        public JsonSettingsRepository Repository { get; }
        public SettingsAuthoritySession Session { get; }
        public InternalSettingsParticipant Participant { get; }

        public static async Task<Lifetime> CreateAsync(DataGenerationDescriptor generation, MutationAdmissionBarrier admission)
        {
            Lifetime lifetime = new(generation, admission);
            try
            {
                Assert.True((await lifetime.Repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
                return lifetime;
            }
            catch { await lifetime.DisposeAsync(); throw; }
        }
        public object? GetService(Type serviceType) => serviceType == typeof(IInternalSettingsReader) ? Participant
            : serviceType == typeof(SettingsGenerationContext) ? _context : null;
        public async ValueTask DisposeAsync() { await Session.DisposeAsync(); Participant.Dispose(); }
    }

    private sealed class CapturingParticipant(InternalSettingsParticipant inner) : ISettingsApplicationParticipant
    {
        public SettingApplicationKind ApplicationKind { get; init; } = SettingApplicationKind.Internal;
        public SettingsApplicationRequest? Request { get; private set; }
        public Func<Task>? BeforeApply { get; init; }
        public bool LoseReply { get; init; }
        public int Applies { get; private set; }
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken cancellationToken)
        { Request = request; return inner.ProbeAsync(request, lease, cancellationToken); }
        public async Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease lease, CancellationToken cancellationToken)
        {
            ++Applies;
            if (BeforeApply is not null) { await BeforeApply(); }
            await inner.ApplyAsync(request, lease, cancellationToken);
            if (LoseReply) { throw new IOException("Isolated lost publication reply."); }
        }
    }
}
