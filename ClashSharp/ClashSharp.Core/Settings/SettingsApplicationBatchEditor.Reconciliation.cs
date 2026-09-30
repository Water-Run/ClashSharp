using System.Security.Cryptography;
using System.Text;

namespace ClashSharp.Settings;

public sealed partial class SettingsApplicationBatchEditor
{
    /// <summary>Invalidates evidence from a previous process and queues observation without removing blocked probes or changing desired values.</summary>
    /// <param name="envelope">Verified persisted envelope being opened by a new process.</param>
    /// <param name="startupId">Fresh nonempty startup identity used for new application batches.</param>
    /// <remarks>Existing running and failed attempts retain their identities and state; failed work still requires explicit retry.</remarks>
    public SettingsEnvelopeEditResult ScheduleStartupReconciliation(SettingsEnvelope envelope, Guid startupId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        return ScheduleObservation(envelope, startupId, envelope.Desired.Keys, startup: true);
    }

    /// <summary>Queues fresh observation of requested runtime values even when their desired values have not changed.</summary>
    /// <remarks>Failed, running and blocked work retains its identity and recovery requirements.</remarks>
    public SettingsEnvelopeEditResult ScheduleRuntimeObservation(
        SettingsEnvelope envelope, IEnumerable<SettingKey> keys, Guid commandId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(keys);
        return ScheduleObservation(envelope, commandId, keys.ToArray(), startup: false);
    }

    private SettingsEnvelopeEditResult ScheduleObservation(
        SettingsEnvelope envelope, Guid observationId, IEnumerable<SettingKey> keys, bool startup)
    {
        if (!_validator.Validate(envelope).IsValid) { return Invalid(envelope, "source_invalid"); }
        if (observationId == Guid.Empty) { return Invalid(envelope, startup ? "startup_identity" : "command_identity"); }
        HashSet<SettingKey> selected = [.. keys];
        if (selected.Any(key => key is null || !envelope.Desired.ContainsKey(key))) { return Invalid(envelope, "observation_key"); }
        HashSet<SettingApplicationKind> unresolvedKinds = startup ? [] : envelope.PendingApplications
            .Where(batch => batch.State is SettingsApplicationBatchState.Running or SettingsApplicationBatchState.Failed
                && batch.Entries.Any(entry => selected.Contains(entry.Key)))
            .Select(batch => batch.ApplicationKind).ToHashSet();
        SettingKey[] staleEvidence = envelope.Applied.Where(pair => selected.Contains(pair.Key)
                && !unresolvedKinds.Contains(_registry.Get(pair.Key.Value).ApplicationKind) && pair.Value.Kind == SettingAppliedStateKind.Verified)
            .Select(pair => pair.Key).ToArray();
        if (staleEvidence.Length == 0) { return SettingsEnvelopeEditResult.NoChange(envelope); }
        if (envelope.EnvelopeRevision == long.MaxValue) { return Invalid(envelope, "revision_exhausted"); }

        Dictionary<SettingKey, SettingAppliedState> applied = new(envelope.Applied);
        HashSet<SettingKey> alreadyCovered = [.. envelope.PendingApplications.SelectMany(batch => batch.Entries).Select(entry => entry.Key)];
        foreach (SettingKey key in staleEvidence)
        {
            applied[key] = SettingAppliedState.Unknown(SettingAppliedUnknownReason.NotObserved, SettingAppliedUnknownHandling.QueueApplication);
        }

        List<SettingsApplicationBatch> batches = [.. envelope.PendingApplications];
        long sequence = batches.Count == 0 ? 0 : batches.Max(batch => batch.CreationSequence);
        foreach (var group in staleEvidence.Where(key => !alreadyCovered.Contains(key))
                     .Select(key => _registry.Get(key.Value))
                     .GroupBy(definition => (definition.ApplicationTiming, definition.ApplicationKind))
                     .OrderBy(group => group.Key.ApplicationTiming).ThenBy(group => group.Key.ApplicationKind))
        {
            SettingsApplicationBatchKind kind = group.Key.ApplicationTiming == SettingApplicationTiming.Restart
                ? SettingsApplicationBatchKind.Restart : SettingsApplicationBatchKind.LiveReconcile;
            SettingsApplicationBatch? pending = startup ? null : batches.FirstOrDefault(batch => batch.Kind == kind
                && batch.ApplicationKind == group.Key.ApplicationKind && batch.State == SettingsApplicationBatchState.Pending
                && batch.Entries.Any(entry => selected.Contains(entry.Key)));
            if (pending is not null)
            {
                // No attempt has started. Keep its identity while adding the accepted companions
                // to the same probe/apply request; unrelated pending batches remain untouched.
                int index = batches.IndexOf(pending);
                batches[index] = new(pending.BatchId, pending.Kind, pending.CreationSequence, pending.AttemptId, pending.State,
                    pending.ApplicationKind, pending.Entries.Concat(group.Select(definition =>
                        SettingsApplicationBatchEntry.Create(definition.Key, envelope.Desired[definition.Key]))));
                continue;
            }
            if (sequence == long.MaxValue) { return Invalid(envelope, "sequence_exhausted"); }
            ++sequence;
            batches.Add(new(
                CreateObservationIdentity(observationId, sequence, "batch", startup),
                kind,
                sequence, CreateObservationIdentity(observationId, sequence, "attempt", startup), SettingsApplicationBatchState.Pending,
                group.Key.ApplicationKind, group.Select(definition => SettingsApplicationBatchEntry.Create(definition.Key, envelope.Desired[definition.Key]))));
        }

        SettingsEnvelope next = new(envelope.SchemaVersion, envelope.EnvelopeRevision + 1, envelope.Desired, applied,
            batches.OrderBy(batch => batch, SettingsApplicationBatchComparer.Instance), envelope.MigrationHistory);
        return _validator.Validate(next).IsValid ? SettingsEnvelopeEditResult.Updated(next) : Invalid(envelope, "result_invalid");
    }

    private static Guid CreateObservationIdentity(Guid observationId, long sequence, string purpose, bool startup)
    {
        string domain = startup ? "clashsharp-settings-startup-v1" : "clashsharp-settings-runtime-observation-v1";
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"{domain}/{observationId:N}/{sequence}/{purpose}")));
        return new(digest.AsSpan(0, 16));
    }
}
