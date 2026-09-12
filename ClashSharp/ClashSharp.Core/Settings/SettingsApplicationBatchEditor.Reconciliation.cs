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
        if (!_validator.Validate(envelope).IsValid) { return Invalid(envelope, "source_invalid"); }
        if (startupId == Guid.Empty) { return Invalid(envelope, "startup_identity"); }
        SettingKey[] staleEvidence = envelope.Applied.Where(pair => pair.Value.Kind == SettingAppliedStateKind.Verified)
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
            if (sequence == long.MaxValue) { return Invalid(envelope, "sequence_exhausted"); }
            ++sequence;
            batches.Add(new(
                CreateStartupIdentity(startupId, sequence, "batch"),
                group.Key.ApplicationTiming == SettingApplicationTiming.Restart ? SettingsApplicationBatchKind.Restart : SettingsApplicationBatchKind.LiveReconcile,
                sequence, CreateStartupIdentity(startupId, sequence, "attempt"), SettingsApplicationBatchState.Pending,
                group.Key.ApplicationKind, group.Select(definition => SettingsApplicationBatchEntry.Create(definition.Key, envelope.Desired[definition.Key]))));
        }

        SettingsEnvelope next = new(envelope.SchemaVersion, envelope.EnvelopeRevision + 1, envelope.Desired, applied,
            batches.OrderBy(batch => batch, SettingsApplicationBatchComparer.Instance), envelope.MigrationHistory);
        return _validator.Validate(next).IsValid ? SettingsEnvelopeEditResult.Updated(next) : Invalid(envelope, "result_invalid");
    }

    private static Guid CreateStartupIdentity(Guid startupId, long sequence, string purpose)
    {
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            FormattableString.Invariant($"clashsharp-settings-startup-v1/{startupId:N}/{sequence}/{purpose}")));
        return new(digest.AsSpan(0, 16));
    }
}
