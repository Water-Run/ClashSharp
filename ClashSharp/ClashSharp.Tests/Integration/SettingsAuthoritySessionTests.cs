using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies durable application decisions against the actual JSON repository with controlled runtime participants.</summary>
public sealed class SettingsAuthoritySessionTests
{
    [Fact]
    public async Task DesiredBatch_IsDurableWithoutClaimingRuntimeApplication()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsAuthorityResult result = await fixture.ChangeAsync(
            [Change("MixedPort", "7890"), Change("TransparentProxyEnabled", "false")]);
        Assert.True(result.IsSucceeded, result.Code);
        SettingsEnvelope reopened = await fixture.ReadAsync();
        Assert.Equal("7890", reopened.Desired[SettingsRegistry.Keys.MixedPort].Value.CanonicalText);
        Assert.Equal("10000", reopened.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
        Assert.Equal(2, Assert.Single(reopened.PendingApplications).Entries.Count);
        Assert.Equal(0, fixture.Runtime.Applies);
        Assert.Equal(0, fixture.Runtime.Probes);
        Assert.Equal(2, fixture.Session.Snapshot.EnvelopeRevision);
    }

    [Fact]
    public async Task Application_PersistsRunningBeforeEffectsAndCompletesOnlyAfterIndependentProbe()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.Runtime.BeforeApply = async (_, _) =>
        {
            SettingsEnvelope during = await fixture.ReadAsync();
            Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single(during.PendingApplications).State);
            Assert.Equal(SettingAppliedStateKind.Unknown, during.Applied[SettingsRegistry.Keys.MixedPort].Kind);
            Assert.Equal("10000", fixture.Runtime.Values[SettingsRegistry.Keys.MixedPort].CanonicalText);
        };

        SettingsAuthorityResult result = await fixture.ApplyAsync(batch);

        Assert.True(result.IsSucceeded, result.Code);
        Assert.Empty(result.Envelope!.PendingApplications);
        Assert.Equal("7890", result.Envelope.Applied[SettingsRegistry.Keys.MixedPort].Value!.CanonicalText);
        Assert.Equal(SettingAppliedValueSource.MutationVerification, result.Envelope.Applied[SettingsRegistry.Keys.MixedPort].Source);
        Assert.Equal(1, fixture.Runtime.Applies);
        Assert.Equal(2, fixture.Runtime.Probes);
        Assert.Equal(Hash(result.Envelope), Hash(await fixture.ReadAsync()));
    }

    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task InterruptedRunningPublication_DoesNotCallParticipantAndFreshSessionResolvesDurableIntent(SettingsPersistenceFaultPoint cut)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.ResetSession(new Fault(cut, occurrence: 1));

        SettingsAuthorityResult interrupted = await fixture.ApplyAsync(batch);

        Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, interrupted.Status);
        Assert.Equal(0, fixture.Runtime.Probes);
        Assert.Equal(0, fixture.Runtime.Applies);
        Assert.Throws<InvalidOperationException>(() => fixture.Session.Snapshot);
        SettingsEnvelope reopened = await fixture.ReadAsync();
        Assert.Equal(cut == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion
            ? SettingsApplicationBatchState.Pending : SettingsApplicationBatchState.Running, Assert.Single(reopened.PendingApplications).State);
        fixture.ResetSession();
        Assert.True((await fixture.ApplyAsync(batch)).IsSucceeded);
        Assert.Equal(1, fixture.Runtime.Applies);
        Assert.Empty((await fixture.ReadAsync()).PendingApplications);
    }

    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task InterruptedVerifiedPublication_FreshSessionDoesNotRepeatAnAlreadyObservedEffect(SettingsPersistenceFaultPoint cut)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.ResetSession(new Fault(cut, occurrence: 2));

        SettingsAuthorityResult interrupted = await fixture.ApplyAsync(batch);

        Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, interrupted.Status);
        Assert.Equal(1, fixture.Runtime.Applies);
        Assert.Throws<InvalidOperationException>(() => fixture.Session.Snapshot);
        fixture.ResetSession();
        SettingsEnvelope reopened = await fixture.ReadAsync();
        if (cut == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)
        {
            Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single(reopened.PendingApplications).State);
            Assert.True((await fixture.ApplyAsync(batch)).IsSucceeded);
            Assert.Equal(3, fixture.Runtime.Probes);
        }
        else
        {
            Assert.Empty(reopened.PendingApplications);
            Assert.Equal(SettingsAuthorityStatus.Rejected, (await fixture.ApplyAsync(batch)).Status);
            Assert.Equal(2, fixture.Runtime.Probes);
        }

        Assert.Equal(1, fixture.Runtime.Applies);
        Assert.Empty((await fixture.ReadAsync()).PendingApplications);
    }

    [Theory]
    [InlineData(SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)]
    [InlineData(SettingsPersistenceFaultPoint.AfterEnvelopePromotion)]
    public async Task Cancellation_ChangesMeaningAtTheDurableRunningBoundary(SettingsPersistenceFaultPoint cut)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        using CancellationTokenSource caller = new();
        fixture.ResetSession(new Fault(cut, occurrence: 1, caller));
        if (cut == SettingsPersistenceFaultPoint.BeforeEnvelopePromotion)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.ApplyAsync(batch, caller.Token));
            Assert.Equal(0, fixture.Runtime.Probes);
            Assert.Equal(0, fixture.Runtime.Applies);
            Assert.Equal(SettingsApplicationBatchState.Pending, Assert.Single((await fixture.ReadAsync()).PendingApplications).State);
        }
        else
        {
            Assert.True((await fixture.ApplyAsync(batch, caller.Token)).IsSucceeded);
            Assert.True(caller.IsCancellationRequested);
            Assert.Equal(1, fixture.Runtime.Applies);
            Assert.Empty((await fixture.ReadAsync()).PendingApplications);
            Assert.All(fixture.Runtime.Tokens, token => Assert.False(token.CanBeCanceled));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostParticipantReply_IsResolvedFromActualEffect(bool effectOccurred)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.Runtime.IgnoreEffect = !effectOccurred;
        fixture.Runtime.ApplyFailure = new IOException("synthetic private reply text");

        SettingsAuthorityResult result = await fixture.ApplyAsync(batch);

        Assert.Equal(effectOccurred ? SettingsAuthorityStatus.Succeeded : SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal(2, fixture.Runtime.Probes);
        if (effectOccurred)
        {
            Assert.Empty(result.Envelope!.PendingApplications);
            Assert.Equal(SettingAppliedValueSource.RuntimeProbe, result.Envelope.Applied[SettingsRegistry.Keys.MixedPort].Source);
            Assert.Equal("settings.application.reply_lost_resolved", result.Code);
        }
        else
        {
            Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single(result.Envelope!.PendingApplications).State);
            Assert.Equal(SettingAppliedStateKind.Unknown, result.Envelope.Applied[SettingsRegistry.Keys.MixedPort].Kind);
            Assert.DoesNotContain("private", result.Code!, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task FailedVerification_RequiresExplicitRetryAndRejectsThePreviousAttempt()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.Runtime.IgnoreEffect = true;
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, (await fixture.ApplyAsync(batch)).Status);
        Assert.Equal(SettingsAuthorityStatus.Rejected, (await fixture.ApplyAsync(batch)).Status);
        Assert.Equal(1, fixture.Runtime.Applies);
        await using (MutationAdmissionLease lease = await fixture.Admission.AcquireOrdinaryAsync(CancellationToken.None))
        {
            Assert.True((await fixture.Session.RetryAdmittedAsync(batch.BatchId, batch.AttemptId, Guid.NewGuid(), lease, CancellationToken.None)).IsSucceeded);
        }

        fixture.Runtime.IgnoreEffect = false;
        Assert.Equal(SettingsAuthorityStatus.Rejected, (await fixture.ApplyAsync(batch)).Status);
        SettingsApplicationBatch retry = Assert.Single((await fixture.ReadAsync()).PendingApplications);
        Assert.True((await fixture.ApplyAsync(retry)).IsSucceeded);
        Assert.Equal(2, fixture.Runtime.Applies);
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("generation_path")]
    [InlineData("batch")]
    [InlineData("attempt")]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("wrong_type")]
    public async Task UnboundOrIncompleteInitialProbe_CannotAuthorizeEffects(string fault)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.Runtime.TransformProbe = observation =>
        {
            DataGenerationDescriptor generation = observation.Generation;
            Guid batchId = observation.BatchId;
            Guid attemptId = observation.AttemptId;
            List<SettingValueChange> values = [.. observation.Values];
            switch (fault)
            {
                case "generation": generation = new(Guid.NewGuid(), generation.GenerationNumber, generation.RootPath); break;
                case "generation_path": generation = new(generation.GenerationId, generation.GenerationNumber, Path.Combine(generation.RootPath, "foreign")); break;
                case "batch": batchId = Guid.NewGuid(); break;
                case "attempt": attemptId = Guid.NewGuid(); break;
                case "missing": values.Clear(); break;
                case "extra": values.Add(Change("AppThemeMode", "Dark")); break;
                case "wrong_type": values[0] = new(SettingsRegistry.Keys.MixedPort, SettingsRegistry.Default.Get("AppThemeMode").DefaultValue); break;
            }

            return new(generation, batchId, attemptId, values);
        };

        SettingsAuthorityResult result = await fixture.ApplyAsync(batch);

        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, result.Status);
        Assert.Equal(0, fixture.Runtime.Applies);
        Assert.Equal(SettingAppliedUnknownReason.ProbeFailed, result.Envelope!.Applied[SettingsRegistry.Keys.MixedPort].UnknownReason);
    }

    [Fact]
    public async Task ProbeFailure_PersistsFailureWithoutBlindlyApplyingDesiredValues()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.Runtime.ProbeFailure = new IOException("not observed");
        Assert.Equal(SettingsAuthorityStatus.ApplicationFailed, (await fixture.ApplyAsync(batch)).Status);
        Assert.Equal(0, fixture.Runtime.Applies);
        Assert.Equal(SettingsApplicationBatchState.Failed, Assert.Single((await fixture.ReadAsync()).PendingApplications).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FatalParticipantGraph_EscapesAndLeavesDurableRunningWork(bool duringApply)
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        AggregateException fatal = new(new IOException("wrapper", Activator.CreateInstance<OutOfMemoryException>()));
        if (duringApply) { fixture.Runtime.ApplyFailure = fatal; }
        else { fixture.Runtime.ProbeFailure = fatal; }

        Assert.Same(fatal, await Record.ExceptionAsync(() => fixture.ApplyAsync(batch)));
        Assert.Equal(SettingsApplicationBatchState.Running, Assert.Single((await fixture.ReadAsync()).PendingApplications).State);
    }

    [Fact]
    public async Task AdmissionDrain_RevokesQueuedEditsButWaitsForTheStartedParticipantAndItsFinalSave()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.BeforeApply = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(deadline.Token);
        };
        Task<SettingsAuthorityResult> applying = fixture.ApplyAsync(batch, deadline.Token);
        Task<SettingsAuthorityResult>? queued = null;
        Task<MutationAdmissionLease>? draining = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            queued = fixture.ChangeAsync([Change("MixedPort", "10001")], deadline.Token);
            Assert.False(queued.IsCompleted);
            draining = fixture.Admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, deadline.Token).AsTask();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(draining.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await applying;
        }

        Assert.True((await applying).IsSucceeded);
        await using MutationAdmissionLease exclusive = await draining!;
        SettingsEnvelope committed = await fixture.ReadAsync();
        Assert.Empty(committed.PendingApplications);
        Assert.Equal("7890", committed.Desired[SettingsRegistry.Keys.MixedPort].Value.CanonicalText);
    }

    [Fact]
    public async Task EmptyAuthority_IsNotInitializedByOrdinaryCommands()
    {
        await using DataGenerationTestDirectory directory = new();
        MutationAdmissionBarrier admission = new();
        await using SettingsAuthoritySession session = new(new JsonSettingsRepository(directory.CreateGeneration(1), SettingsRegistry.Default), SettingsRegistry.Default, admission);
        Assert.Throws<InvalidOperationException>(() => session.Snapshot);
        await using MutationAdmissionLease lease = await admission.AcquireOrdinaryAsync(CancellationToken.None);
        SettingsAuthorityResult result = await session.ChangeAdmittedAsync([Change("MixedPort", "7890")], Guid.NewGuid(), lease, CancellationToken.None);
        Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, result.Status);
        Assert.Equal(SettingsPersistenceStatus.Invalid, result.PersistenceStatus);
        Assert.Equal("settings.authority.uninitialized", result.Code);
        Assert.Throws<InvalidOperationException>(() => session.Snapshot);
    }

    [Fact]
    public async Task ConcurrentRepositoryWriter_IsPreservedAndRunningAttemptIsReprobedAfterConflict()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        fixture.Runtime.BeforeApply = async (_, _) =>
        {
            JsonSettingsRepository other = new(fixture.Generation, SettingsRegistry.Default);
            SettingsEnvelope current = (await other.OpenAsync(CancellationToken.None)).Envelope!;
            SettingsEnvelope next = new SettingsEnvelopeEditor(SettingsRegistry.Default).ApplyChanges(current,
                [Change("AppThemeMode", "Dark")], Guid.NewGuid()).Envelope;
            Assert.True((await other.SaveAsync(next, current.EnvelopeRevision, CancellationToken.None)).IsSucceeded);
        };

        SettingsAuthorityResult conflicted = await fixture.ApplyAsync(batch);

        Assert.Equal(SettingsAuthorityStatus.PersistenceFailed, conflicted.Status);
        Assert.Equal(SettingsPersistenceStatus.Conflict, conflicted.PersistenceStatus);
        Assert.Equal("Dark", fixture.Session.Snapshot.Desired[SettingsRegistry.Keys.AppThemeMode].Value.CanonicalText);
        Assert.Equal(2, fixture.Session.Snapshot.PendingApplications.Count);
        fixture.Runtime.BeforeApply = null;
        Assert.True((await fixture.ApplyAsync(batch)).IsSucceeded);
        Assert.Equal(1, fixture.Runtime.Applies);
        SettingsApplicationBatch retained = Assert.Single((await fixture.ReadAsync()).PendingApplications);
        Assert.Equal(SettingApplicationKind.Appearance, retained.ApplicationKind);
    }

    [Fact]
    public async Task RestartBatch_IsDeferredLiveAndRequiresExclusiveStartupOwnership()
    {
        await using DataGenerationTestDirectory directory = new();
        SettingsRegistry registry = SettingsEnvelopeTestData.CreateLiveAndRestartRegistry();
        DataGenerationDescriptor generation = directory.CreateGeneration(1);
        JsonSettingsRepository repository = new(generation, registry);
        Assert.True((await repository.SaveAsync(SettingsEnvelopeTestData.CreateMatchingEnvelope(registry), 0, CancellationToken.None)).IsSucceeded);
        MutationAdmissionBarrier admission = new();
        await using SettingsAuthoritySession session = new(repository, registry, admission);
        SettingKey key = new("RestartInternal");
        Participant runtime = new() { ApplicationKind = SettingApplicationKind.Internal };
        runtime.Values[key] = registry.Get(key.Value).DefaultValue;
        SettingsApplicationBatch batch;
        await using (MutationAdmissionLease ordinary = await admission.AcquireOrdinaryAsync(CancellationToken.None))
        {
            SettingsAuthorityResult changed = await session.ChangeAdmittedAsync(
                [new(key, registry.Get(key.Value).Normalize("true").Value!)], Guid.NewGuid(), ordinary, CancellationToken.None);
            Assert.True(changed.IsSucceeded, changed.Code);
            batch = Assert.Single(changed.Envelope!.PendingApplications);
            SettingsAuthorityResult deferred = await session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId,
                runtime, SettingsApplicationPhase.Live, ordinary, CancellationToken.None);
            Assert.Equal(SettingsAuthorityStatus.DeferredToRestart, deferred.Status);
            Assert.Equal(0, runtime.Probes);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId,
                runtime, SettingsApplicationPhase.Startup, ordinary, CancellationToken.None));
        }

        await using MutationAdmissionLease startup = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
        SettingsAuthorityResult applied = await session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId,
            runtime, SettingsApplicationPhase.Startup, startup, CancellationToken.None);
        Assert.True(applied.IsSucceeded, applied.Code);
        Assert.Equal(SettingAppliedValueSource.StartupReconciliation, applied.Envelope!.Applied[key].Source);
        Assert.Empty(applied.Envelope.PendingApplications);
    }

    [Fact]
    public async Task SessionRetirement_WaitsForApplicationAndRejectsStaleWriters()
    {
        await using Fixture fixture = await Fixture.CreateAsync();
        SettingsApplicationBatch batch = await fixture.QueueAsync();
        using CancellationTokenSource deadline = new(TimeSpan.FromSeconds(15));
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Runtime.BeforeApply = async (_, _) =>
        {
            entered.TrySetResult();
            await release.Task.WaitAsync(deadline.Token);
        };
        Task<SettingsAuthorityResult> applying = fixture.ApplyAsync(batch, deadline.Token);
        Task? retirement = null;
        try
        {
            await entered.Task.WaitAsync(deadline.Token);
            retirement = fixture.Session.DisposeAsync().AsTask();
            Assert.False(retirement.IsCompleted);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => fixture.ChangeAsync([Change("MixedPort", "10001")], deadline.Token));
        }
        finally
        {
            release.TrySetResult();
            await applying;
            if (retirement is not null) { await retirement; }
        }

        Assert.True((await applying).IsSucceeded);
        Assert.Throws<ObjectDisposedException>(() => fixture.Session.Snapshot);
        Assert.Empty((await fixture.ReadAsync()).PendingApplications);
        await fixture.Session.DisposeAsync();
    }

    private static SettingValueChange Change(string key, string value) => new(new(key), SettingsEnvelopeTestData.Value(key, value));
    private static string Hash(SettingsEnvelope envelope) => SettingsEnvelopeCodec.Encode(envelope, SettingsRegistry.Default).ContentHash;

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly DataGenerationTestDirectory _directory = new();
        private readonly List<SettingsAuthoritySession> _previousSessions = [];
        private Fixture()
        {
            Generation = _directory.CreateGeneration(1);
            Session = new(new JsonSettingsRepository(Generation, SettingsRegistry.Default), SettingsRegistry.Default, Admission);
        }

        public DataGenerationDescriptor Generation { get; }
        public MutationAdmissionBarrier Admission { get; } = new();
        public SettingsAuthoritySession Session { get; private set; }
        public Participant Runtime { get; } = new();

        public static async Task<Fixture> CreateAsync()
        {
            Fixture fixture = new();
            try
            {
                Assert.True((await new JsonSettingsRepository(fixture.Generation, SettingsRegistry.Default).SaveAsync(
                    SettingsEnvelopeTestData.CreateMatchingEnvelope(), 0, CancellationToken.None)).IsSucceeded);
                return fixture;
            }
            catch
            {
                await fixture.DisposeAsync();
                throw;
            }
        }

        public void ResetSession(ISettingsPersistenceFaultInjector? fault = null)
        {
            _previousSessions.Add(Session);
            Session = new(new JsonSettingsRepository(Generation, SettingsRegistry.Default, fault), SettingsRegistry.Default, Admission);
        }

        public async Task<SettingsEnvelope> ReadAsync() =>
            (await new JsonSettingsRepository(Generation, SettingsRegistry.Default).OpenAsync(CancellationToken.None)).Envelope!;

        public async Task<SettingsAuthorityResult> ChangeAsync(IEnumerable<SettingValueChange> changes, CancellationToken token = default)
        {
            await using MutationAdmissionLease lease = await Admission.AcquireOrdinaryAsync(token);
            return await Session.ChangeAdmittedAsync(changes, Guid.NewGuid(), lease, token);
        }

        public async Task<SettingsApplicationBatch> QueueAsync() =>
            Assert.Single((await ChangeAsync([Change("MixedPort", "7890")])).Envelope!.PendingApplications);

        public async Task<SettingsAuthorityResult> ApplyAsync(SettingsApplicationBatch batch, CancellationToken token = default)
        {
            await using MutationAdmissionLease lease = await Admission.AcquireOrdinaryAsync(token);
            return await Session.ApplyBatchAdmittedAsync(batch.BatchId, batch.AttemptId, Runtime, SettingsApplicationPhase.Live, lease, token);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            foreach (SettingsAuthoritySession previous in _previousSessions) { await previous.DisposeAsync(); }
            await _directory.DisposeAsync();
        }
    }

    private sealed class Participant : ISettingsApplicationParticipant
    {
        public SettingApplicationKind ApplicationKind { get; init; } = SettingApplicationKind.Network;
        public Dictionary<SettingKey, SettingValue> Values { get; } = SettingsRegistry.Default.Definitions.ToDictionary(item => item.Key, item => item.DefaultValue);
        public int Probes { get; private set; }
        public int Applies { get; private set; }
        public bool IgnoreEffect { get; set; }
        public Exception? ApplyFailure { get; set; }
        public Exception? ProbeFailure { get; set; }
        public Func<SettingsApplicationRequest, CancellationToken, Task>? BeforeApply { get; set; }
        public Func<SettingsApplicationObservation, SettingsApplicationObservation>? TransformProbe { get; set; }
        public List<CancellationToken> Tokens { get; } = [];

        public Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            ++Probes;
            Tokens.Add(cancellationToken);
            if (ProbeFailure is not null) { throw ProbeFailure; }
            SettingsApplicationObservation observation = new(request.Generation, request.Batch.BatchId, request.Batch.AttemptId,
                request.Values.Keys.Select(key => new SettingValueChange(key, Values[key])));
            return Task.FromResult(TransformProbe?.Invoke(observation) ?? observation);
        }

        public async Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            ++Applies;
            Tokens.Add(cancellationToken);
            if (BeforeApply is not null) { await BeforeApply(request, cancellationToken); }
            if (!IgnoreEffect)
            {
                foreach ((SettingKey key, SettingValue value) in request.Values) { Values[key] = value; }
            }

            if (ApplyFailure is not null) { throw ApplyFailure; }
        }
    }

    private sealed class Fault(SettingsPersistenceFaultPoint selected, int occurrence, CancellationTokenSource? cancellation = null) : ISettingsPersistenceFaultInjector
    {
        private int _arrivals;
        public Task InjectAsync(SettingsPersistenceFaultPoint point, CancellationToken cancellationToken)
        {
            if (point == selected && ++_arrivals == occurrence)
            {
                if (cancellation is null) { throw new IOException("controlled durable cut"); }
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        }
    }
}
