using ClashSharp.Settings;
using ClashSharp.Tests.Unit.Settings;

namespace ClashSharp.Tests.Unit.Libraries;

/// <summary>
/// Checks the pending-batch lifecycle as a closed state machine: every state x event x attempt-identity
/// combination is classified by an explicit oracle, and random event walks must keep envelope invariants.
/// </summary>
public sealed class SettingsBatchLifecycleContractTests
{
    private static readonly SettingsRegistry Registry = SettingsRegistry.Default;
    private static readonly SettingsApplicationBatchEditor BatchEditor = new(Registry);
    private static readonly SettingsEnvelopeEditor Editor = new(Registry);
    private static readonly SettingsEnvelopeValidator Validator = new(Registry);
    private static readonly DateTimeOffset ObservedAt = new(2026, 11, 9, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Lifecycle events accepted by the batch editor.</summary>
    public enum LifecycleEvent
    {
        /// <summary>Begin the attempt.</summary>
        Begin,

        /// <summary>Fail the attempt.</summary>
        Fail,

        /// <summary>Complete the attempt with matching evidence.</summary>
        Complete,

        /// <summary>Retry the failed attempt.</summary>
        Retry,
    }

    /// <summary>Which attempt identity the caller presents.</summary>
    public enum AttemptReference
    {
        /// <summary>The identity currently stored in the envelope.</summary>
        Current,

        /// <summary>An identity that was never issued for the batch.</summary>
        StaleAttempt,

        /// <summary>A batch identity that does not exist.</summary>
        UnknownBatch,
    }

    /// <summary>Enumerates the full state x event x identity universe from the enum definitions.</summary>
    /// <returns>Theory rows.</returns>
    public static TheoryData<SettingsApplicationBatchState, LifecycleEvent, AttemptReference> Universe()
    {
        TheoryData<SettingsApplicationBatchState, LifecycleEvent, AttemptReference> data = [];
        foreach (SettingsApplicationBatchState state in Enum.GetValues<SettingsApplicationBatchState>())
        {
            foreach (LifecycleEvent lifecycleEvent in Enum.GetValues<LifecycleEvent>())
            {
                foreach (AttemptReference reference in Enum.GetValues<AttemptReference>())
                {
                    data.Add(state, lifecycleEvent, reference);
                }
            }
        }

        return data;
    }

    /// <summary>Verifies every state x event x identity result against the explicit transition table.</summary>
    [Theory]
    [MemberData(nameof(Universe))]
    public void Transition_MatchesTheClosedStateMachineTable(
        SettingsApplicationBatchState state,
        LifecycleEvent lifecycleEvent,
        AttemptReference reference)
    {
        SettingsEnvelope source = EnvelopeIn(state);
        SettingsApplicationBatch batch = Assert.Single(source.PendingApplications);
        Guid newAttempt = new("30000000-0000-0000-0000-0000000000aa");

        SettingsEnvelopeEditResult result = Run(source, batch, lifecycleEvent, reference, newAttempt);

        Expectation expected = Expect(state, lifecycleEvent, reference);
        Assert.Equal(expected.Outcome, result.Outcome);
        if (expected.Outcome == SettingsEnvelopeEditOutcome.Invalid)
        {
            Assert.Equal(expected.ErrorCode, result.ErrorCode);
        }

        if (expected.Outcome != SettingsEnvelopeEditOutcome.Updated)
        {
            Assert.Same(source, result.Envelope);
            return;
        }

        Assert.Equal(source.EnvelopeRevision + 1, result.Envelope.EnvelopeRevision);
        Assert.True(Validator.Validate(result.Envelope).IsValid);
        Assert.All(source.Desired, pair => Assert.Same(pair.Value, result.Envelope.Desired[pair.Key]));
        if (expected.NextState is null)
        {
            Assert.Empty(result.Envelope.PendingApplications);
            return;
        }

        SettingsApplicationBatch next = Assert.Single(result.Envelope.PendingApplications);
        Assert.Equal(expected.NextState, next.State);
        Assert.Equal(batch.BatchId, next.BatchId);
        Assert.Equal(batch.CreationSequence, next.CreationSequence);
        Assert.Equal(batch.Kind, next.Kind);
        Assert.Equal(batch.ApplicationKind, next.ApplicationKind);
        Assert.Equal(batch.Entries, next.Entries);
        Assert.Equal(lifecycleEvent == LifecycleEvent.Retry, next.AttemptId != batch.AttemptId);
        Assert.Equal(next.State == SettingsApplicationBatchState.Failed, next.LastError is not null);
    }

    /// <summary>Verifies the oracle itself covers every declared state and event, so a new member forces a decision.</summary>
    [Fact]
    public void Oracle_ClassifiesEveryDeclaredState()
    {
        foreach (SettingsApplicationBatchState state in Enum.GetValues<SettingsApplicationBatchState>())
        {
            foreach (LifecycleEvent lifecycleEvent in Enum.GetValues<LifecycleEvent>())
            {
                _ = Expect(state, lifecycleEvent, AttemptReference.Current);
            }
        }
    }

    /// <summary>Verifies all four events leave evidence for exactly the batch keys and never touch unrelated keys.</summary>
    [Fact]
    public void Evidence_IsRewrittenOnlyForKeysOfTheAdvancedBatch()
    {
        SettingsEnvelope pending = EnvelopeIn(SettingsApplicationBatchState.Pending);
        SettingsApplicationBatch batch = Assert.Single(pending.PendingApplications);
        HashSet<SettingKey> batchKeys = [.. batch.Entries.Select(static entry => entry.Key)];

        SettingsEnvelope running = AssertUpdated(BatchEditor.BeginAttempt(pending, batch.BatchId, batch.AttemptId));
        AssertEvidence(pending, running, batchKeys, SettingAppliedUnknownReason.NotObserved);
        SettingsEnvelope failed = AssertUpdated(BatchEditor.FailAttempt(
            running, batch.BatchId, batch.AttemptId, new SettingsApplicationError("test.failure")));
        AssertEvidence(running, failed, batchKeys, SettingAppliedUnknownReason.ProbeFailed);
        Assert.Equal("test.failure", Assert.Single(failed.PendingApplications).LastError!.Code);

        SettingsEnvelope retried = AssertUpdated(BatchEditor.RetryFailed(
            failed, batch.BatchId, batch.AttemptId, Guid.NewGuid()));
        Assert.All(failed.Applied, pair => Assert.Same(pair.Value, retried.Applied[pair.Key]));
        Assert.Null(Assert.Single(retried.PendingApplications).LastError);

        SettingsApplicationBatch retriedBatch = retried.PendingApplications[0];
        SettingsEnvelope running2 = AssertUpdated(BatchEditor.BeginAttempt(retried, batch.BatchId, retriedBatch.AttemptId));
        SettingsEnvelope completed = AssertUpdated(BatchEditor.CompleteAttempt(
            running2,
            batch.BatchId,
            retriedBatch.AttemptId,
            batch.Entries.Select(entry => new SettingValueChange(entry.Key, running2.Desired[entry.Key].Value)),
            SettingAppliedValueSource.RuntimeProbe,
            ObservedAt));
        Assert.Empty(completed.PendingApplications);
        foreach (SettingKey key in batchKeys)
        {
            SettingAppliedState applied = completed.Applied[key];
            Assert.Equal(SettingAppliedStateKind.Verified, applied.Kind);
            Assert.Equal(completed.Desired[key].Value, applied.Value);
            Assert.Equal(SettingsGenerators.Hash(completed.Desired[key].Value), applied.ObservedHash);
            Assert.Equal(ObservedAt, applied.ObservedAt);
            Assert.Equal(SettingAppliedValueSource.RuntimeProbe, applied.Source);
        }

        Assert.All(
            running2.Applied.Where(pair => !batchKeys.Contains(pair.Key)),
            pair => Assert.Same(pair.Value, completed.Applied[pair.Key]));
    }

    /// <summary>Verifies only the three observation sources that imply a real probe are accepted as completion evidence.</summary>
    [Fact]
    public void Complete_AcceptsOnlyObservationSourcesThatRepresentARealProbe()
    {
        SettingsEnvelope running = EnvelopeIn(SettingsApplicationBatchState.Running);
        SettingsApplicationBatch batch = running.PendingApplications[0];
        IReadOnlyList<SettingValueChange> observed = ObservedValues(running, batch);
        HashSet<SettingAppliedValueSource> accepted =
        [
            SettingAppliedValueSource.RuntimeProbe,
            SettingAppliedValueSource.MutationVerification,
            SettingAppliedValueSource.StartupReconciliation,
        ];

        foreach (SettingAppliedValueSource source in Enum.GetValues<SettingAppliedValueSource>())
        {
            SettingsEnvelopeEditResult result = BatchEditor.CompleteAttempt(
                running, batch.BatchId, batch.AttemptId, observed, source, ObservedAt);

            Assert.Equal(
                accepted.Contains(source) ? SettingsEnvelopeEditOutcome.Updated : SettingsEnvelopeEditOutcome.Invalid,
                result.Outcome);
            if (!accepted.Contains(source))
            {
                Assert.Same(running, result.Envelope);
            }
        }
    }

    /// <summary>Verifies observation timestamps must be present and expressed in UTC.</summary>
    [Fact]
    public void Complete_RequiresUtcObservationTime()
    {
        SettingsEnvelope running = EnvelopeIn(SettingsApplicationBatchState.Running);
        SettingsApplicationBatch batch = running.PendingApplications[0];
        IReadOnlyList<SettingValueChange> observed = ObservedValues(running, batch);
        DateTimeOffset[] invalid =
        [
            default,
            new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.FromHours(8)),
            new DateTimeOffset(2026, 11, 9, 8, 0, 0, TimeSpan.FromMinutes(-1)),
        ];

        foreach (DateTimeOffset time in invalid)
        {
            SettingsEnvelopeEditResult result = BatchEditor.CompleteAttempt(
                running, batch.BatchId, batch.AttemptId, observed, SettingAppliedValueSource.RuntimeProbe, time);
            Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, result.Outcome);
            Assert.Same(running, result.Envelope);
        }

        Assert.Equal(
            SettingsEnvelopeEditOutcome.Updated,
            BatchEditor.CompleteAttempt(
                running, batch.BatchId, batch.AttemptId, observed, SettingAppliedValueSource.RuntimeProbe, DateTimeOffset.MaxValue.ToUniversalTime()).Outcome);
    }

    /// <summary>Verifies a completion carrying any strict subset, superset or perturbation of the evidence is rejected atomically.</summary>
    [Fact]
    public void Complete_RejectsEveryPartialOrPerturbedEvidenceSet()
    {
        SettingsEnvelope running = EnvelopeIn(SettingsApplicationBatchState.Running);
        SettingsApplicationBatch batch = running.PendingApplications[0];
        List<SettingValueChange> exact = [.. ObservedValues(running, batch)];

        for (int mask = 0; mask < (1 << exact.Count); mask++)
        {
            List<SettingValueChange> subset = [.. exact.Where((_, index) => (mask & (1 << index)) != 0)];
            SettingsEnvelopeEditResult result = BatchEditor.CompleteAttempt(
                running, batch.BatchId, batch.AttemptId, subset, SettingAppliedValueSource.MutationVerification, ObservedAt);

            Assert.Equal(
                mask == (1 << exact.Count) - 1 ? SettingsEnvelopeEditOutcome.Updated : SettingsEnvelopeEditOutcome.Invalid,
                result.Outcome);
            if (result.Outcome == SettingsEnvelopeEditOutcome.Invalid)
            {
                Assert.Same(running, result.Envelope);
            }
        }

        foreach (SettingValueChange change in exact)
        {
            SettingDefinition definition = Registry.Get(change.Key.Value);
            foreach (SettingValue other in SettingsGenerators.Candidates(definition).Where(value => !value.Equals(change.Value)))
            {
                List<SettingValueChange> perturbed = [.. exact.Select(item => item.Key == change.Key ? new SettingValueChange(item.Key, other) : item)];
                SettingsEnvelopeEditResult result = BatchEditor.CompleteAttempt(
                    running, batch.BatchId, batch.AttemptId, perturbed, SettingAppliedValueSource.RuntimeProbe, ObservedAt);
                Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, result.Outcome);
                Assert.Equal("settings.application.observation_mismatch", result.ErrorCode);
                Assert.Same(running, result.Envelope);
            }
        }

        SettingKey unrelated = Registry.Definitions.First(definition => exact.TrueForAll(item => item.Key != definition.Key)).Key;
        List<SettingValueChange> superset =
        [
            .. exact,
            new SettingValueChange(unrelated, running.Desired[unrelated].Value),
        ];
        Assert.Equal(
            SettingsEnvelopeEditOutcome.Invalid,
            BatchEditor.CompleteAttempt(running, batch.BatchId, batch.AttemptId, superset, SettingAppliedValueSource.RuntimeProbe, ObservedAt).Outcome);
        Assert.Equal(
            SettingsEnvelopeEditOutcome.Invalid,
            BatchEditor.CompleteAttempt(running, batch.BatchId, batch.AttemptId, [.. exact, exact[0]], SettingAppliedValueSource.RuntimeProbe, ObservedAt).Outcome);
    }

    /// <summary>Verifies a retry cannot reuse any attempt identity that already exists in the envelope.</summary>
    [Fact]
    public void Retry_RequiresAFreshNonemptyAttemptIdentity()
    {
        SettingsEnvelope failed = EnvelopeIn(SettingsApplicationBatchState.Failed);
        SettingsApplicationBatch batch = failed.PendingApplications[0];

        foreach (Guid identity in new[] { Guid.Empty, batch.AttemptId })
        {
            SettingsEnvelopeEditResult result = BatchEditor.RetryFailed(failed, batch.BatchId, batch.AttemptId, identity);
            Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, result.Outcome);
            Assert.Equal("settings.application.retry_identity", result.ErrorCode);
            Assert.Same(failed, result.Envelope);
        }

        SettingsEnvelope withSibling = AssertUpdated(Editor.ApplyChanges(
            failed,
            [new SettingValueChange(SettingsRegistry.Keys.MixedPort, SettingsEnvelopeTestData.Value("MixedPort", "7891"))],
            Guid.NewGuid()));
        SettingsApplicationBatch sibling = Assert.Single(withSibling.PendingApplications, item => item.BatchId != batch.BatchId);
        Assert.Equal(
            SettingsEnvelopeEditOutcome.Invalid,
            BatchEditor.RetryFailed(withSibling, batch.BatchId, batch.AttemptId, sibling.AttemptId).Outcome);
    }

    /// <summary>Verifies that a retry invalidates every callback of the superseded attempt.</summary>
    [Fact]
    public void Retry_InvalidatesCallbacksOfThePreviousAttempt()
    {
        SettingsEnvelope pending = EnvelopeIn(SettingsApplicationBatchState.Pending);
        SettingsApplicationBatch batch = pending.PendingApplications[0];
        List<Guid> attempts = [batch.AttemptId];
        SettingsEnvelope current = pending;
        for (int round = 0; round < 5; round++)
        {
            Guid attempt = attempts[^1];
            current = AssertUpdated(BatchEditor.BeginAttempt(current, batch.BatchId, attempt));
            current = AssertUpdated(BatchEditor.FailAttempt(current, batch.BatchId, attempt, new SettingsApplicationError("round." + round)));
            Guid next = new(0x40000000 + round, 0, 0, [0, 0, 0, 0, 0, 0, 0, 1]);
            current = AssertUpdated(BatchEditor.RetryFailed(current, batch.BatchId, attempt, next));
            attempts.Add(next);

            foreach (Guid superseded in attempts.Take(attempts.Count - 1))
            {
                AssertStale(current, BatchEditor.BeginAttempt(current, batch.BatchId, superseded));
                AssertStale(current, BatchEditor.FailAttempt(current, batch.BatchId, superseded, new SettingsApplicationError("x")));
                AssertStale(current, BatchEditor.RetryFailed(current, batch.BatchId, superseded, Guid.NewGuid()));
                AssertStale(
                    current,
                    BatchEditor.CompleteAttempt(
                        current, batch.BatchId, superseded, [], SettingAppliedValueSource.RuntimeProbe, ObservedAt));
            }
        }

        Assert.Equal(attempts.Count, attempts.Distinct().Count());
        Assert.Equal(pending.PendingApplications[0].CreationSequence, current.PendingApplications[0].CreationSequence);
    }

    /// <summary>Verifies every reachable lifecycle node of the machine is valid and each declared state is reachable.</summary>
    [Fact]
    public void ReachableGraph_CoversEveryStateAndEveryNodeIsAValidEnvelope()
    {
        SettingsEnvelope root = EnvelopeIn(SettingsApplicationBatchState.Pending);
        HashSet<(SettingsApplicationBatchState State, Guid Attempt)> seen = [];
        HashSet<SettingsApplicationBatchState> reached = [];
        Queue<(SettingsEnvelope Envelope, int Depth)> queue = new([(root, 0)]);
        int counter = 0;
        bool completionObserved = false;

        while (queue.TryDequeue(out (SettingsEnvelope Envelope, int Depth) node))
        {
            if (node.Envelope.PendingApplications.Count == 0)
            {
                completionObserved = true;
                Assert.True(Validator.Validate(node.Envelope).IsValid);
                continue;
            }

            SettingsApplicationBatch batch = node.Envelope.PendingApplications[0];
            reached.Add(batch.State);
            Assert.True(Validator.Validate(node.Envelope).IsValid);
            if (!seen.Add((batch.State, batch.AttemptId)) || node.Depth > 12)
            {
                continue;
            }

            foreach (LifecycleEvent lifecycleEvent in Enum.GetValues<LifecycleEvent>())
            {
                Guid fresh = new(0x50000000 + ++counter, 0, 0, [0, 0, 0, 0, 0, 0, 0, 2]);
                SettingsEnvelopeEditResult result = Run(node.Envelope, batch, lifecycleEvent, AttemptReference.Current, fresh);
                Assert.True(result.Outcome != SettingsEnvelopeEditOutcome.Busy);
                if (result.Outcome == SettingsEnvelopeEditOutcome.Updated)
                {
                    Assert.Equal(node.Envelope.EnvelopeRevision + 1, result.Envelope.EnvelopeRevision);
                    queue.Enqueue((result.Envelope, node.Depth + 1));
                }
            }
        }

        Assert.True(completionObserved, "A batch must be removable only through completion evidence.");
        Assert.Equal(
            [.. Enum.GetValues<SettingsApplicationBatchState>().Order()],
            [.. reached.Order()]);
    }

    /// <summary>Verifies the batch constructor rejects every inconsistent state/error pairing.</summary>
    [Theory]
    [InlineData(SettingsApplicationBatchState.Pending, true)]
    [InlineData(SettingsApplicationBatchState.Running, true)]
    [InlineData(SettingsApplicationBatchState.Failed, false)]
    public void BatchConstruction_BindsLastErrorExactlyToTheFailedState(
        SettingsApplicationBatchState state,
        bool errorSupplied)
    {
        SettingsApplicationBatchEntry entry = SettingsApplicationBatchEntry.Create(
            SettingsRegistry.Keys.AppThemeMode,
            new SettingDesiredEntry(SettingsEnvelopeTestData.Value("AppThemeMode", "Dark"), 2));

        Assert.Throws<ArgumentException>(() => new SettingsApplicationBatch(
            Guid.NewGuid(),
            SettingsApplicationBatchKind.LiveReconcile,
            1,
            Guid.NewGuid(),
            state,
            SettingApplicationKind.Appearance,
            [entry],
            errorSupplied ? new SettingsApplicationError("code") : null));
    }

    /// <summary>Verifies key revisions, batch binding and evidence invariants survive long seeded random event walks.</summary>
    /// <param name="seedOffset">Offset added to the fixed seed.</param>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void RandomEventWalk_PreservesRevisionBindingAndEvidenceInvariants(int seedOffset)
    {
        Random random = new(SettingsGenerators.Seed + seedOffset);
        IReadOnlyList<SettingDefinition> mutable = SettingsGenerators.MutableDefinitions(Registry);
        Assert.True(mutable.Count > 8);
        SettingsEnvelope current = SettingsEnvelopeTestData.CreateMatchingEnvelope();
        Dictionary<SettingKey, (SettingValue Value, long Revision)> modelDesired =
            current.Desired.ToDictionary(pair => pair.Key, pair => (pair.Value.Value, pair.Value.KeyDesiredRevision));
        HashSet<string> outcomes = [];

        for (int step = 0; step < 260; step++)
        {
            SettingsEnvelope before = current;
            int choice = random.Next(10);
            string label;
            SettingsEnvelopeEditResult result;
            if (choice <= 3 || before.PendingApplications.Count == 0)
            {
                (result, label) = ApplyRandomChanges(before, random, mutable, modelDesired);
            }
            else if (choice == 4)
            {
                (result, label) = RevertRandomKeys(before, random, mutable, modelDesired);
            }
            else if (choice == 5)
            {
                SettingKey[] keys = [.. mutable.Where(_ => random.Next(3) == 0).Select(static definition => definition.Key)];
                result = BatchEditor.ScheduleRuntimeObservation(before, keys, SettingsGenerators.NextGuid(random));
                label = "observe";
                AssertEvidenceNeverFabricated(before, result.Envelope);
            }
            else
            {
                (result, label) = AdvanceRandomBatch(before, random);
            }

            outcomes.Add($"{label}:{result.Outcome}");
            Assert.True(
                result.Outcome == SettingsEnvelopeEditOutcome.Updated
                    ? result.Envelope.EnvelopeRevision == before.EnvelopeRevision + 1
                    : ReferenceEquals(before, result.Envelope),
                $"step {step} {label}: revision must advance exactly once on Updated and never otherwise");
            current = result.Envelope;
            AssertInvariants(current, modelDesired);
        }

        Assert.Contains(outcomes, static item => item.EndsWith(":Updated", StringComparison.Ordinal));
        Assert.Contains(outcomes, static item => item.StartsWith("apply:", StringComparison.Ordinal));
        Assert.Contains(outcomes, static item => item.StartsWith("advance-", StringComparison.Ordinal));
    }

    private static (SettingsEnvelopeEditResult Result, string Label) ApplyRandomChanges(
        SettingsEnvelope before,
        Random random,
        IReadOnlyList<SettingDefinition> mutable,
        Dictionary<SettingKey, (SettingValue Value, long Revision)> modelDesired)
    {
        int count = 1 + random.Next(4);
        Dictionary<SettingKey, SettingValue> requested = [];
        while (requested.Count < count)
        {
            SettingDefinition definition = mutable[random.Next(mutable.Count)];
            requested[definition.Key] = SettingsGenerators.Pick(random, definition);
        }

        SettingsEnvelopeEditResult result = Editor.ApplyChanges(
            before,
            requested.Select(pair => new SettingValueChange(pair.Key, pair.Value)),
            SettingsGenerators.NextGuid(random));
        AssertEditExpectation(before, result, requested, modelDesired);
        return (result, "apply");
    }

    private static (SettingsEnvelopeEditResult Result, string Label) RevertRandomKeys(
        SettingsEnvelope before,
        Random random,
        IReadOnlyList<SettingDefinition> mutable,
        Dictionary<SettingKey, (SettingValue Value, long Revision)> modelDesired)
    {
        SettingKey[] keys = [.. mutable.OrderBy(_ => random.Next()).Take(1 + random.Next(5)).Select(static definition => definition.Key)];
        Dictionary<SettingKey, SettingValue> target = [];
        foreach (SettingKey key in keys)
        {
            SettingAppliedState applied = before.Applied[key];
            target[key] = applied.Kind == SettingAppliedStateKind.Verified
                ? applied.Value!
                : Registry.Get(key.Value).SafeFallback;
        }

        SettingsEnvelopeEditResult result = Editor.Revert(before, keys, SettingsGenerators.NextGuid(random));
        AssertEditExpectation(before, result, target, modelDesired);
        return (result, "revert");
    }

    private static void AssertEditExpectation(
        SettingsEnvelope before,
        SettingsEnvelopeEditResult result,
        Dictionary<SettingKey, SettingValue> requested,
        Dictionary<SettingKey, (SettingValue Value, long Revision)> modelDesired)
    {
        HashSet<SettingKey> changed = [.. requested.Where(pair => !modelDesired[pair.Key].Value.Equals(pair.Value)).Select(static pair => pair.Key)];
        bool touchesRunning = before.PendingApplications.Any(batch =>
            batch.State == SettingsApplicationBatchState.Running && batch.Entries.Any(entry => changed.Contains(entry.Key)));
        SettingsEnvelopeEditOutcome expected = changed.Count == 0
            ? SettingsEnvelopeEditOutcome.NoChange
            : touchesRunning ? SettingsEnvelopeEditOutcome.Busy : SettingsEnvelopeEditOutcome.Updated;

        Assert.Equal(expected, result.Outcome);
        Assert.Equal(
            expected == SettingsEnvelopeEditOutcome.Updated,
            result.Envelope.EnvelopeRevision == before.EnvelopeRevision + 1);
        if (expected != SettingsEnvelopeEditOutcome.Updated)
        {
            return;
        }

        foreach (SettingKey key in changed)
        {
            modelDesired[key] = (requested[key], modelDesired[key].Revision + 1);
        }

        Assert.All(before.Applied, pair => Assert.Same(pair.Value, result.Envelope.Applied[pair.Key]));
        IEnumerable<SettingsApplicationBatch> untouchedRunning = before.PendingApplications.Where(batch => batch.State == SettingsApplicationBatchState.Running);
        foreach (SettingsApplicationBatch running in untouchedRunning)
        {
            Assert.Contains(result.Envelope.PendingApplications, batch => ReferenceEquals(batch, running));
        }
    }

    private static (SettingsEnvelopeEditResult Result, string Label) AdvanceRandomBatch(SettingsEnvelope before, Random random)
    {
        SettingsApplicationBatch batch = before.PendingApplications[random.Next(before.PendingApplications.Count)];
        switch (batch.State)
        {
            case SettingsApplicationBatchState.Pending:
                return (BatchEditor.BeginAttempt(before, batch.BatchId, batch.AttemptId), "advance-begin");
            case SettingsApplicationBatchState.Running when random.Next(4) == 0:
                return (BatchEditor.FailAttempt(before, batch.BatchId, batch.AttemptId, new SettingsApplicationError("walk.failure")), "advance-fail");
            case SettingsApplicationBatchState.Running:
                SettingsEnvelopeEditResult completed = BatchEditor.CompleteAttempt(
                    before,
                    batch.BatchId,
                    batch.AttemptId,
                    ObservedValues(before, batch),
                    SettingAppliedValueSource.RuntimeProbe,
                    ObservedAt);
                Assert.Equal(SettingsEnvelopeEditOutcome.Updated, completed.Outcome);
                foreach (SettingsApplicationBatchEntry entry in batch.Entries)
                {
                    Assert.Equal(before.Desired[entry.Key].Value, completed.Envelope.Applied[entry.Key].Value);
                    Assert.Equal(SettingAppliedStateKind.Verified, completed.Envelope.Applied[entry.Key].Kind);
                }

                return (completed, "advance-complete");
            default:
                return (BatchEditor.RetryFailed(before, batch.BatchId, batch.AttemptId, SettingsGenerators.NextGuid(random)), "advance-retry");
        }
    }

    private static void AssertInvariants(
        SettingsEnvelope envelope,
        Dictionary<SettingKey, (SettingValue Value, long Revision)> modelDesired)
    {
        SettingsEnvelopeValidationResult validation = Validator.Validate(envelope);
        Assert.True(validation.IsValid, string.Join(";", validation.Errors.Select(static error => error.Code + "@" + error.Path)));
        foreach ((SettingKey key, (SettingValue value, long revision)) in modelDesired)
        {
            Assert.Equal(value, envelope.Desired[key].Value);
            Assert.Equal(revision, envelope.Desired[key].KeyDesiredRevision);
        }

        HashSet<SettingKey> covered = [];
        foreach (SettingsApplicationBatch batch in envelope.PendingApplications)
        {
            foreach (SettingsApplicationBatchEntry entry in batch.Entries)
            {
                Assert.True(covered.Add(entry.Key), "a key may belong to one pending batch only");
                Assert.Equal(envelope.Desired[entry.Key].KeyDesiredRevision, entry.KeyDesiredRevision);
                Assert.Equal(SettingsGenerators.Hash(envelope.Desired[entry.Key].Value), entry.ValueHash);
            }

            if (batch.State == SettingsApplicationBatchState.Running)
            {
                Assert.All(
                    batch.Entries,
                    entry => Assert.Equal(SettingAppliedStateKind.Unknown, envelope.Applied[entry.Key].Kind));
            }
        }

        foreach (SettingDefinition definition in Registry.Definitions)
        {
            SettingAppliedState applied = envelope.Applied[definition.Key];
            if (applied.Kind == SettingAppliedStateKind.Verified
                && !applied.Value!.Equals(envelope.Desired[definition.Key].Value))
            {
                Assert.Contains(definition.Key, covered);
            }
        }
    }

    private static void AssertEvidenceNeverFabricated(SettingsEnvelope before, SettingsEnvelope after)
    {
        foreach ((SettingKey key, SettingAppliedState previous) in before.Applied)
        {
            SettingAppliedState next = after.Applied[key];
            if (previous.Kind == SettingAppliedStateKind.Unknown)
            {
                Assert.Equal(SettingAppliedStateKind.Unknown, next.Kind);
            }
            else if (next.Kind == SettingAppliedStateKind.Verified)
            {
                Assert.Same(previous, next);
            }
        }
    }

    private static void AssertEvidence(
        SettingsEnvelope before,
        SettingsEnvelope after,
        IReadOnlySet<SettingKey> advancedKeys,
        SettingAppliedUnknownReason reason)
    {
        foreach ((SettingKey key, SettingAppliedState previous) in before.Applied)
        {
            if (advancedKeys.Contains(key))
            {
                Assert.Equal(SettingAppliedStateKind.Unknown, after.Applied[key].Kind);
                Assert.Equal(reason, after.Applied[key].UnknownReason);
                Assert.True(after.Applied[key].RequiresApplication);
            }
            else
            {
                Assert.Same(previous, after.Applied[key]);
            }
        }
    }

    private static void AssertStale(SettingsEnvelope source, SettingsEnvelopeEditResult result)
    {
        Assert.Equal(SettingsEnvelopeEditOutcome.Invalid, result.Outcome);
        Assert.Equal("settings.application.stale_attempt", result.ErrorCode);
        Assert.Same(source, result.Envelope);
    }

    private static SettingsEnvelope AssertUpdated(SettingsEnvelopeEditResult result)
    {
        Assert.True(result.Outcome == SettingsEnvelopeEditOutcome.Updated, result.ErrorCode + "@" + result.ErrorPath);
        return result.Envelope;
    }

    private static IReadOnlyList<SettingValueChange> ObservedValues(SettingsEnvelope envelope, SettingsApplicationBatch batch) =>
        [.. batch.Entries.Select(entry => new SettingValueChange(entry.Key, envelope.Desired[entry.Key].Value))];

    private static SettingsEnvelope EnvelopeIn(SettingsApplicationBatchState state) =>
        SettingsEnvelopeTestData.CreatePendingEnvelope(
            [("AppThemeMode", "Dark"), ("AppAccentColorValue", "#FF001122")],
            state,
            state == SettingsApplicationBatchState.Failed ? new SettingsApplicationError("test.failure") : null);

    private static SettingsEnvelopeEditResult Run(
        SettingsEnvelope envelope,
        SettingsApplicationBatch batch,
        LifecycleEvent lifecycleEvent,
        AttemptReference reference,
        Guid newAttempt)
    {
        Guid batchId = reference == AttemptReference.UnknownBatch ? new Guid("99999999-0000-0000-0000-000000000009") : batch.BatchId;
        Guid attempt = reference == AttemptReference.StaleAttempt ? new Guid("88888888-0000-0000-0000-000000000008") : batch.AttemptId;
        return lifecycleEvent switch
        {
            LifecycleEvent.Begin => BatchEditor.BeginAttempt(envelope, batchId, attempt),
            LifecycleEvent.Fail => BatchEditor.FailAttempt(envelope, batchId, attempt, new SettingsApplicationError("test.failure")),
            LifecycleEvent.Complete => BatchEditor.CompleteAttempt(
                envelope, batchId, attempt, ObservedValues(envelope, batch), SettingAppliedValueSource.RuntimeProbe, ObservedAt),
            LifecycleEvent.Retry => BatchEditor.RetryFailed(envelope, batchId, attempt, newAttempt),
            _ => throw new NotSupportedException(lifecycleEvent.ToString()),
        };
    }

    private static Expectation Expect(
        SettingsApplicationBatchState state,
        LifecycleEvent lifecycleEvent,
        AttemptReference reference)
    {
        if (reference != AttemptReference.Current)
        {
            return new(SettingsEnvelopeEditOutcome.Invalid, "settings.application.stale_attempt", state);
        }

        const string wrongState = "settings.application.state";
        return (state, lifecycleEvent) switch
        {
            (SettingsApplicationBatchState.Pending, LifecycleEvent.Begin) => new(SettingsEnvelopeEditOutcome.Updated, null, SettingsApplicationBatchState.Running),
            (SettingsApplicationBatchState.Running, LifecycleEvent.Begin) => new(SettingsEnvelopeEditOutcome.NoChange, null, state),
            (SettingsApplicationBatchState.Failed, LifecycleEvent.Begin) => new(SettingsEnvelopeEditOutcome.Invalid, wrongState, state),
            (SettingsApplicationBatchState.Running, LifecycleEvent.Fail) => new(SettingsEnvelopeEditOutcome.Updated, null, SettingsApplicationBatchState.Failed),
            (SettingsApplicationBatchState.Pending or SettingsApplicationBatchState.Failed, LifecycleEvent.Fail) => new(SettingsEnvelopeEditOutcome.Invalid, wrongState, state),
            (SettingsApplicationBatchState.Running, LifecycleEvent.Complete) => new(SettingsEnvelopeEditOutcome.Updated, null, null),
            (SettingsApplicationBatchState.Pending or SettingsApplicationBatchState.Failed, LifecycleEvent.Complete) => new(SettingsEnvelopeEditOutcome.Invalid, wrongState, state),
            (SettingsApplicationBatchState.Failed, LifecycleEvent.Retry) => new(SettingsEnvelopeEditOutcome.Updated, null, SettingsApplicationBatchState.Pending),
            (SettingsApplicationBatchState.Pending or SettingsApplicationBatchState.Running, LifecycleEvent.Retry) => new(SettingsEnvelopeEditOutcome.Invalid, wrongState, state),
            _ => throw new NotSupportedException($"Unclassified lifecycle transition {state} x {lifecycleEvent}; extend the oracle."),
        };
    }

    private sealed record Expectation(
        SettingsEnvelopeEditOutcome Outcome,
        string? ErrorCode,
        SettingsApplicationBatchState? NextState);
}
