using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

public sealed partial class SettingsAuthoritySession
{
    /// <summary>Continues a multi-participant explicit retry after its first durable retry decision.</summary>
    internal Task<SettingsAuthorityResult> ContinueRetryAdmittedAsync(Guid batchId, Guid expectedAttemptId,
        Guid newAttemptId, MutationAdmissionLease lease) => ExecuteAdmittedAsync(lease, async token =>
        {
            SettingsAuthorityResult read = await ReadCoreAsync(token).ConfigureAwait(false);
            return !read.IsSucceeded ? read : await PersistEditAsync(read.Envelope!,
                _batches.RetryFailed(read.Envelope!, batchId, expectedAttemptId, newAttemptId), token).ConfigureAwait(false);
        }, honorRevocation: false, CancellationToken.None);

    /// <summary>Includes accepted companion values without consuming unrelated pending or blocked intent.</summary>
    internal SettingKey[] GetRuntimeObservationKeys(SettingsEnvelope envelope, IEnumerable<SettingKey> keys)
    {
        HashSet<SettingKey> requested = [.. keys];
        HashSet<SettingApplicationKind> kinds = requested.Select(key => _registry.Get(key.Value).ApplicationKind).ToHashSet();
        HashSet<SettingKey> pending = [.. envelope.PendingApplications.SelectMany(batch => batch.Entries).Select(entry => entry.Key)];
        return _registry.Definitions.Where(definition => requested.Contains(definition.Key)
                || kinds.Contains(definition.ApplicationKind) && !pending.Contains(definition.Key)
                    && envelope.Applied[definition.Key].Kind == SettingAppliedStateKind.Verified
                    && envelope.Applied[definition.Key].Value!.Equals(envelope.Desired[definition.Key].Value))
            .Select(definition => definition.Key).ToArray();
    }

    /// <summary>Retains a committed command while persisting its fresh runtime observation intent.</summary>
    internal Task<SettingsAuthorityResult> PrepareRuntimeObservationAdmittedAsync(
        IEnumerable<SettingKey> keys, Guid commandId, MutationAdmissionLease lease,
        bool continueCommittedCommand, CancellationToken cancellationToken)
    {
        SettingKey[] requested = keys.ToArray();
        return ExecuteAdmittedAsync(lease, async token =>
        {
            SettingsAuthorityResult read = await ReadCoreAsync(token).ConfigureAwait(false);
            return !read.IsSucceeded ? read : await PersistEditAsync(read.Envelope!,
                _batches.ScheduleRuntimeObservation(read.Envelope!, requested, commandId), token).ConfigureAwait(false);
        }, honorRevocation: !continueCommittedCommand, cancellationToken);
    }
}
