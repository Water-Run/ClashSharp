using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

public sealed partial class GenerationSettingsAuthority
{
    /// <inheritdoc />
    public Task<SettingsAuthorityResult> ApplyRuntimeChangesAsync(
        IEnumerable<SettingValueChange> changes, Guid commandId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        return ExecuteConsumerAsync((context, lease, token) => ChangeAndObserveAsync(context, snapshot, commandId, lease, token),
            RequiresProducerDrain(snapshot.Select(change => change.Key)), cancellationToken);
    }

    /// <inheritdoc />
    public Task<SettingsAuthorityResult> ApplyRuntimeChangesAdmittedAsync(
        IEnumerable<SettingValueChange> changes, Guid commandId,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        if (RequiresProducerDrain(snapshot.Select(change => change.Key))) { _admission.EnsureActiveExclusiveLease(admissionLease); }
        return ExecuteAdmittedAsync((context, lease, token) => ChangeAndObserveAsync(context, snapshot, commandId, lease, token),
            admissionLease, cancellationToken);
    }

    private static async Task<SettingsAuthorityResult> ChangeAndObserveAsync(
        SettingsGenerationContext context, SettingValueChange[] changes, Guid commandId,
        MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        SettingsAuthorityResult changed = await context.Session.ChangeAdmittedAsync(changes, commandId, lease, cancellationToken).ConfigureAwait(false);
        if (!changed.IsSucceeded) { return changed; }
        bool committed = changed.Status == SettingsAuthorityStatus.Succeeded;
        HashSet<SettingKey> keys = context.Session.GetRuntimeObservationKeys(changed.Envelope!, changes.Select(change => change.Key)).ToHashSet();
        SettingsAuthorityResult prepared = await context.Session.PrepareRuntimeObservationAdmittedAsync(
            keys, commandId, lease, continueCommittedCommand: committed,
            committed ? CancellationToken.None : cancellationToken).ConfigureAwait(false);
        SettingsAuthorityResult applied = await ApplyAffectedAsync(context, prepared, keys, SettingsApplicationPhase.Live, lease).ConfigureAwait(false);
        if (!applied.IsSucceeded) { return applied; }
        SettingsEnvelope envelope = applied.Envelope!;
        return keys.All(key => envelope.Applied[key].Kind == SettingAppliedStateKind.Verified
                && envelope.Applied[key].Value!.Equals(envelope.Desired[key].Value))
            ? applied
            : new(SettingsAuthorityStatus.ApplicationFailed, envelope, "settings.application.unverified_runtime_effect");
    }
}
