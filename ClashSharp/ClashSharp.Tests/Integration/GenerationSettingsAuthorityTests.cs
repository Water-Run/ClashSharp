using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Checks complete consumer commands through actual generation and settings stores.</summary>
public sealed class GenerationSettingsAuthorityTests
{
    [Fact]
    public async Task CompleteChangeSet_VerifiesAllAffectedParticipantsWithinOneAuthorityCommand()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsAuthorityResult result = await fixture.Authority.ApplyChangesAsync(
            [Change("MixedPort", "7890"), Change("AppThemeMode", "Dark")], Guid.NewGuid(), CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal("7890", result.Envelope.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
        Assert.Equal("Dark", result.Envelope.Applied[SettingsRegistry.Keys.AppThemeMode].Value!.CanonicalText);
        Assert.Equal(1, fixture.Participants[SettingApplicationKind.Network].Applies);
        Assert.Equal(1, fixture.Participants[SettingApplicationKind.Appearance].Applies);
        Assert.Equal(fixture.Manifest.Descriptor.GenerationId, fixture.Authority.CaptureSnapshot().Generation.GenerationId);
        Assert.Equal(Hash(result.Envelope), Hash((await fixture.Repository.OpenAsync(CancellationToken.None)).Envelope!));
    }

    [Fact]
    public async Task DrainAfterDesiredCommit_DoesNotAbandonTheCommittedApplication()
    {
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        PublicationHook hook = new();
        await using Fixture fixture = await Fixture.CreateAsync(hook);
        Task<MutationAdmissionLease>? drain = null;
        hook.AfterFirstPublication = () =>
        {
            drain = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, deadline.Token).AsTask();
            Assert.False(drain.IsCompleted);
        };

        try
        {
            SettingsAuthorityResult result = await fixture.Authority.ApplyChangesAsync(
                [Change("MixedPort", "7890"), Change("AppThemeMode", "Dark")], Guid.NewGuid(), deadline.Token);
            Assert.True(result.IsSucceeded, result.Code);
            Assert.Empty(result.Envelope!.PendingApplications);
            Assert.Equal(1, fixture.Participants[SettingApplicationKind.Network].Applies);
            Assert.Equal(1, fixture.Participants[SettingApplicationKind.Appearance].Applies);
        }
        finally
        {
            if (drain is not null) { await using MutationAdmissionLease lease = await drain; }
        }
    }

    [Fact]
    public async Task PageCancellationAfterDesiredCommit_StillObservesTheCompleteCommand()
    {
        using CancellationTokenSource caller = new();
        PublicationHook hook = new();
        await using Fixture fixture = await Fixture.CreateAsync(hook);
        hook.AfterFirstPublication = caller.Cancel;
        SettingsAuthorityResult result = await fixture.Authority.ApplyChangesAsync([Change("MixedPort", "7890")], Guid.NewGuid(), caller.Token);
        Assert.True(caller.IsCancellationRequested);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal("7890", result.Envelope.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
    }

    [Fact]
    public async Task FailedLaterParticipant_PreservesEarlierEvidenceAndRetryTouchesOnlyTheFailedAttempt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        fixture.Participants[SettingApplicationKind.Network].IgnoreEffects = true;
        SettingsAuthorityResult failed = await fixture.Authority.ApplyChangesAsync(
            [Change("AppThemeMode", "Dark"), Change("MixedPort", "7890")], Guid.NewGuid(), CancellationToken.None);
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, failed.Status);
        SettingsApplicationBatch batch = Assert.Single(failed.Envelope!.PendingApplications);
        Assert.Equal(SettingApplicationKind.Network, batch.ApplicationKind);
        Assert.Equal("Dark", failed.Envelope.Applied[SettingsRegistry.Keys.AppThemeMode].Value!.CanonicalText);
        Assert.Equal(SettingAppliedStateKind.Unknown, failed.Envelope.Applied[SettingsRegistry.Keys.MixedPort].Kind);
        fixture.Participants[SettingApplicationKind.Network].IgnoreEffects = false;
        SettingsAuthorityResult retried = await fixture.Authority.RetryAsync(batch.BatchId, batch.AttemptId, Guid.NewGuid(), CancellationToken.None);
        Assert.True(retried.IsSucceeded, retried.Code);
        Assert.Empty(retried.Envelope!.PendingApplications);
        Assert.Equal(1, fixture.Participants[SettingApplicationKind.Appearance].Applies);
        Assert.Equal(2, fixture.Participants[SettingApplicationKind.Network].Applies);
    }

    [Fact]
    public async Task QueuedCallerChanges_AreCopiedBeforeTheFirstAsynchronousWait()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Participants[SettingApplicationKind.Network].BeforeApply = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(deadline.Token);
        };
        Task<SettingsAuthorityResult> first = fixture.Authority.ApplyChangesAsync([Change("MixedPort", "7890")], Guid.NewGuid(), deadline.Token);
        Task<SettingsAuthorityResult>? second = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            List<SettingValueChange> input = [Change("MixedPort", "10001")];
            second = fixture.Authority.ApplyChangesAsync(input, Guid.NewGuid(), deadline.Token);
            input[0] = Change("MixedPort", "10002");
            Assert.False(second.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (second is not null) { await second; }
        }

        Assert.True((await second!).IsSucceeded);
        Assert.Equal("10001", fixture.Authority.CaptureSnapshot().Envelope.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
    }

    [Fact]
    public async Task GenerationDrain_WaitsUntilTheRuntimeEffectAndFinalSaveAreBothFinished()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Participants[SettingApplicationKind.Network].BeforeApply = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(deadline.Token);
        };
        Task<SettingsAuthorityResult> command = fixture.Authority.ApplyChangesAsync([Change("MixedPort", "7890")], Guid.NewGuid(), deadline.Token);
        Task<DataGenerationTransition>? drain = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            drain = fixture.Generations.BeginDrainAsync(fixture.Manifest.ContentHash, deadline.Token).AsTask();
            Assert.False(drain.IsCompleted);
            Assert.Throws<DataGenerationManagerException>(() => fixture.Authority.CaptureSnapshot());
        }
        finally
        {
            release.TrySetResult();
            await command;
        }

        Assert.True((await command).IsSucceeded);
        DataGenerationTransition transition = await drain!;
        SettingsEnvelope durable = (await fixture.Repository.OpenAsync(deadline.Token)).Envelope!;
        Assert.Empty(durable.PendingApplications);
        Assert.Equal("7890", durable.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
        await transition.AbortAsync();
    }

    [Fact]
    public async Task QueuedCommand_IsRevokedWithoutChangingTheCommittedTargetDuringDrain()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Participants[SettingApplicationKind.Network].BeforeApply = async () =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(deadline.Token);
        };
        Task<SettingsAuthorityResult> first = fixture.Authority.ApplyChangesAsync([Change("MixedPort", "7890")], Guid.NewGuid(), deadline.Token);
        Task<MutationAdmissionLease>? drain = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            Task<SettingsAuthorityResult> queued = fixture.Authority.ApplyChangesAsync([Change("MixedPort", "10001")], Guid.NewGuid(), deadline.Token);
            Assert.False(queued.IsCompleted);
            drain = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, deadline.Token).AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(drain.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await first;
            if (drain is not null) { await using MutationAdmissionLease lease = await drain; }
        }

        Assert.True((await first).IsSucceeded);
        Assert.Equal("7890", fixture.Authority.CaptureSnapshot().Envelope.Desired[SettingsRegistry.Keys.MixedPort].Value.CanonicalText);
        Assert.Equal(1, fixture.Participants[SettingApplicationKind.Network].Applies);
    }

    [Fact]
    public async Task MisboundGenerationService_IsRejectedBeforeItCanOpenTheForeignRepository()
    {
        await using Fixture fixture = await Fixture.CreateAsync(misbind: true);
        string foreignSettings = Path.Combine(fixture.Session.Generation.RootPath, "Settings");
        Assert.False(Directory.Exists(foreignSettings));
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Authority.OpenAsync(CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => fixture.Authority.CaptureSnapshot());
        Assert.False(Directory.Exists(foreignSettings));
    }

    [Fact]
    public async Task Startup_ReobservesAllValuesThroughTheGenerationLocalParticipants()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        await using MutationAdmissionLease startup = await fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        SettingsAuthorityResult result = await fixture.Authority.ReconcileStartupAdmittedAsync(Guid.NewGuid(), startup, CancellationToken.None);
        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.All(result.Envelope.Applied.Values, state => Assert.Equal(SettingAppliedValueSource.StartupReconciliation, state.Source));
        Assert.All(fixture.Participants.Values, participant =>
        {
            Assert.Equal(0, participant.Applies);
            Assert.Equal(1, participant.Probes);
        });
    }

    private static SettingValueChange Change(string key, string value) => new(new(key), SettingsEnvelopeTestData.Value(key, value));
    private static string Hash(SettingsEnvelope value) => SettingsEnvelopeCodec.Encode(value, SettingsRegistry.Default).ContentHash;

    private sealed class Fixture : IAsyncDisposable, IServiceProvider
    {
        private readonly DataGenerationTestDirectory _directory = new();
        private SettingsGenerationContext? _context;
        public DataGenerationManifestSnapshot Manifest { get; private set; } = null!;
        public SettingsAuthoritySession Session { get; private set; } = null!;
        public JsonSettingsRepository Repository { get; private set; } = null!;
        public MutationAdmissionBarrier Admission { get; } = new();
        public DataGenerationManager Generations { get; } = new();
        public GenerationSettingsAuthority Authority { get; private set; } = null!;
        public Dictionary<SettingApplicationKind, Participant> Participants { get; } = Enum.GetValues<SettingApplicationKind>().ToDictionary(kind => kind, kind => new Participant(kind));
        public object? GetService(Type serviceType) => serviceType == typeof(SettingsGenerationContext) ? _context : null;

        public static async Task<Fixture> CreateAsync(PublicationHook? hook = null, bool misbind = false)
        {
            Fixture fixture = new();
            try
            {
                fixture.Manifest = await fixture._directory.PromoteFirstAsync();
                JsonSettingsRepository seed = new(fixture.Manifest.Descriptor, SettingsRegistry.Default);
                Assert.True((await seed.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
                fixture.Repository = new(fixture.Manifest.Descriptor, SettingsRegistry.Default, hook);
                ISettingsRepository repository = misbind
                    ? new JsonSettingsRepository(fixture._directory.CreateGeneration(2), SettingsRegistry.Default) : fixture.Repository;
                fixture.Session = new(repository, SettingsRegistry.Default, fixture.Admission);
                fixture._context = new(fixture.Session, fixture.Participants.Values);
                fixture.Generations.Initialize(fixture.Manifest, new(fixture.Manifest.Descriptor, new Lifetime(fixture)));
                fixture.Authority = new(fixture.Generations, fixture.Admission);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public async ValueTask DisposeAsync()
        {
            await Generations.DisposeAsync();
            await _directory.DisposeAsync();
        }

        private sealed class Lifetime(Fixture fixture) : IServiceProvider, IAsyncDisposable
        {
            public object? GetService(Type serviceType) => fixture.GetService(serviceType);
            public ValueTask DisposeAsync() => fixture.Session.DisposeAsync();
        }
    }

    private sealed class Participant(SettingApplicationKind kind) : ISettingsApplicationParticipant
    {
        private readonly Dictionary<SettingKey, SettingValue> _values = SettingsRegistry.Default.Definitions.ToDictionary(definition => definition.Key, definition => definition.DefaultValue);
        public SettingApplicationKind ApplicationKind => kind;
        public int Applies { get; private set; }
        public int Probes { get; private set; }
        public bool IgnoreEffects { get; set; }
        public Func<Task>? BeforeApply { get; set; }
        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            ++Probes;
            return Task.FromResult(new SettingsApplicationObservation(request.Generation, request.Batch.BatchId, request.Batch.AttemptId,
                request.Values.Keys.Select(key => new SettingValueChange(key, _values[key]))));
        }

        public async Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            ++Applies;
            if (BeforeApply is not null) { await BeforeApply(); }
            if (!IgnoreEffects)
            {
                foreach ((SettingKey key, SettingValue value) in request.Values) { _values[key] = value; }
            }
        }
    }

    private sealed class PublicationHook : ISettingsPersistenceFaultInjector
    {
        private int _publications;
        public Action? AfterFirstPublication { get; set; }
        public Task InjectAsync(SettingsPersistenceFaultPoint point, CancellationToken cancellationToken)
        {
            if (point == SettingsPersistenceFaultPoint.AfterEnvelopePromotion && ++_publications == 1) { AfterFirstPublication?.Invoke(); }
            return Task.CompletedTask;
        }
    }
}
