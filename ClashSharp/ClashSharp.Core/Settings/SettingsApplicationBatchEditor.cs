namespace ClashSharp.Settings;

/// <summary>Advances exact application attempts without changing desired values or unrelated batch identities.</summary>
public sealed partial class SettingsApplicationBatchEditor
{
    private readonly SettingsRegistry _registry;
    private readonly SettingsEnvelopeValidator _validator;

    /// <summary>Creates a pure lifecycle editor using the canonical schema.</summary>
    /// <param name="registry">Immutable setting definitions.</param>
    public SettingsApplicationBatchEditor(SettingsRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _validator = new(registry);
    }

    /// <summary>Marks a pending attempt as possibly side-effecting before the participant is invoked.</summary>
    /// <param name="envelope">Validated source envelope.</param>
    /// <param name="batchId">Exact batch to begin.</param>
    /// <param name="attemptId">Expected current attempt; stale callbacks cannot begin a replacement attempt.</param>
    public SettingsEnvelopeEditResult BeginAttempt(SettingsEnvelope envelope, Guid batchId, Guid attemptId)
    {
        SettingsEnvelopeEditResult? invalid = FindAttempt(envelope, batchId, attemptId, out SettingsApplicationBatch? batch);
        if (invalid is not null) { return invalid; }
        if (batch!.State == SettingsApplicationBatchState.Running) { return SettingsEnvelopeEditResult.NoChange(envelope); }
        if (batch.State != SettingsApplicationBatchState.Pending) { return Invalid(envelope, "state"); }

        return Rewrite(envelope, ReplaceState(batch, SettingsApplicationBatchState.Running),
            InvalidateEvidence(envelope, batch, SettingAppliedUnknownReason.NotObserved));
    }

    /// <summary>Retains failed work with unknown effective state; an attempted effect is never reported as rolled back without evidence.</summary>
    /// <param name="envelope">Validated source envelope.</param>
    /// <param name="batchId">Exact running batch.</param>
    /// <param name="attemptId">Expected current attempt.</param>
    /// <param name="error">Stable diagnostic chosen by the participant boundary.</param>
    public SettingsEnvelopeEditResult FailAttempt(SettingsEnvelope envelope, Guid batchId, Guid attemptId, SettingsApplicationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        SettingsEnvelopeEditResult? invalid = FindAttempt(envelope, batchId, attemptId, out SettingsApplicationBatch? batch);
        if (invalid is not null) { return invalid; }
        if (batch!.State != SettingsApplicationBatchState.Running) { return Invalid(envelope, "state"); }

        return Rewrite(envelope, ReplaceState(batch, SettingsApplicationBatchState.Failed, error),
            InvalidateEvidence(envelope, batch, SettingAppliedUnknownReason.ProbeFailed));
    }

    /// <summary>Removes one running batch only when every independently observed value matches its exact desired revision and hash.</summary>
    /// <param name="envelope">Validated source envelope.</param>
    /// <param name="batchId">Exact running batch.</param>
    /// <param name="attemptId">Expected current attempt.</param>
    /// <param name="observedValues">Complete independently obtained canonical values; duplicate or unrelated keys are rejected.</param>
    /// <param name="source">Runtime probe, mutation verification, or startup reconciliation evidence.</param>
    /// <param name="observedAt">Nondefault UTC time of observation.</param>
    public SettingsEnvelopeEditResult CompleteAttempt(
        SettingsEnvelope envelope,
        Guid batchId,
        Guid attemptId,
        IEnumerable<SettingValueChange> observedValues,
        SettingAppliedValueSource source,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(observedValues);
        SettingsEnvelopeEditResult? invalid = FindAttempt(envelope, batchId, attemptId, out SettingsApplicationBatch? batch);
        if (invalid is not null) { return invalid; }
        if (batch!.State != SettingsApplicationBatchState.Running) { return Invalid(envelope, "state"); }
        if (source is not (SettingAppliedValueSource.RuntimeProbe or SettingAppliedValueSource.MutationVerification
            or SettingAppliedValueSource.StartupReconciliation)) { return Invalid(envelope, "observation_source"); }
        if (observedAt == default || observedAt.Offset != TimeSpan.Zero) { return Invalid(envelope, "observation_time"); }

        Dictionary<SettingKey, SettingValue> observed = [];
        foreach (SettingValueChange change in observedValues)
        {
            if (change is null || !observed.TryAdd(change.Key, change.Value)) { return Invalid(envelope, "observation_keys"); }
        }

        if (observed.Count != batch.Entries.Count) { return Invalid(envelope, "observation_keys"); }
        Dictionary<SettingKey, SettingAppliedState> applied = new(envelope.Applied);
        foreach (SettingsApplicationBatchEntry entry in batch.Entries)
        {
            if (!observed.TryGetValue(entry.Key, out SettingValue? value)) { return Invalid(envelope, "observation_keys"); }
            SettingDefinition definition = _registry.Get(entry.Key.Value);
            SettingNormalizationResult normalized = definition.Normalize(value.CanonicalText);
            if (!normalized.IsSuccess || !value.Equals(normalized.Value)
                || !value.Equals(envelope.Desired[entry.Key].Value)
                || SettingsApplicationBatchEntry.ComputeValueHash(value) != entry.ValueHash)
            {
                return Invalid(envelope, "observation_mismatch");
            }

            applied[entry.Key] = SettingAppliedState.Verified(value, source, entry.ValueHash, observedAt);
        }

        return Rewrite(envelope, replacement: null, applied, batchId);
    }

    /// <summary>Authorizes a failed batch to retry under a new attempt identity while retaining key revisions.</summary>
    /// <param name="envelope">Validated source envelope.</param>
    /// <param name="batchId">Failed batch to retry.</param>
    /// <param name="expectedAttemptId">Identity of the failed attempt being retried.</param>
    /// <param name="newAttemptId">Fresh nonempty identity that invalidates previous attempt callbacks.</param>
    public SettingsEnvelopeEditResult RetryFailed(SettingsEnvelope envelope, Guid batchId, Guid expectedAttemptId, Guid newAttemptId)
    {
        SettingsEnvelopeEditResult? invalid = FindAttempt(envelope, batchId, expectedAttemptId, out SettingsApplicationBatch? batch);
        if (invalid is not null) { return invalid; }
        if (batch!.State != SettingsApplicationBatchState.Failed) { return Invalid(envelope, "state"); }
        if (newAttemptId == Guid.Empty || envelope.PendingApplications.Any(item => item.AttemptId == newAttemptId))
        {
            return Invalid(envelope, "retry_identity");
        }

        SettingsApplicationBatch next = new(batch.BatchId, batch.Kind, batch.CreationSequence, newAttemptId,
            SettingsApplicationBatchState.Pending, batch.ApplicationKind, batch.Entries);
        return Rewrite(envelope, next, envelope.Applied);
    }

    private SettingsEnvelopeEditResult? FindAttempt(
        SettingsEnvelope envelope, Guid batchId, Guid attemptId, out SettingsApplicationBatch? batch)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        batch = null;
        if (!_validator.Validate(envelope).IsValid) { return Invalid(envelope, "source_invalid"); }
        batch = envelope.PendingApplications.SingleOrDefault(item => item.BatchId == batchId);
        return batch is null || batch.AttemptId != attemptId ? Invalid(envelope, "stale_attempt") : null;
    }

    private static SettingsApplicationBatch ReplaceState(
        SettingsApplicationBatch batch, SettingsApplicationBatchState state, SettingsApplicationError? error = null) =>
        new(batch.BatchId, batch.Kind, batch.CreationSequence, batch.AttemptId, state, batch.ApplicationKind, batch.Entries, error);

    private static Dictionary<SettingKey, SettingAppliedState> InvalidateEvidence(
        SettingsEnvelope envelope, SettingsApplicationBatch batch, SettingAppliedUnknownReason reason)
    {
        Dictionary<SettingKey, SettingAppliedState> result = new(envelope.Applied);
        foreach (SettingsApplicationBatchEntry entry in batch.Entries)
        {
            result[entry.Key] = SettingAppliedState.Unknown(reason, SettingAppliedUnknownHandling.QueueApplication);
        }

        return result;
    }

    private SettingsEnvelopeEditResult Rewrite(
        SettingsEnvelope source, SettingsApplicationBatch? replacement,
        IEnumerable<KeyValuePair<SettingKey, SettingAppliedState>> applied, Guid? removedBatchId = null)
    {
        if (source.EnvelopeRevision == long.MaxValue) { return Invalid(source, "revision_exhausted"); }
        SettingsEnvelope next = new(source.SchemaVersion, source.EnvelopeRevision + 1, source.Desired, applied,
            source.PendingApplications.Where(batch => batch.BatchId != removedBatchId)
                .Select(batch => batch.BatchId == replacement?.BatchId ? replacement! : batch), source.MigrationHistory);
        return _validator.Validate(next).IsValid ? SettingsEnvelopeEditResult.Updated(next) : Invalid(source, "result_invalid");
    }

    private static SettingsEnvelopeEditResult Invalid(SettingsEnvelope source, string code) =>
        SettingsEnvelopeEditResult.Invalid(source, "settings.application." + code);
}
