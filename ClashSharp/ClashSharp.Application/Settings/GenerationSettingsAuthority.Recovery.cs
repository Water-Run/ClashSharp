using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

public sealed partial class GenerationSettingsAuthority : ISettingsApplicationRecovery
{
    /// <inheritdoc />
    public async Task<SettingsApplicationRecoveryResult> RetryApplicationAsync(SettingsAuthoritySnapshot expected,
        Guid batchId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(expected);
        SettingsApplicationBatch selected = expected.Envelope.PendingApplications.SingleOrDefault(batch => batch.BatchId == batchId
            && batch.State == SettingsApplicationBatchState.Failed)
            ?? throw new ArgumentException("The displayed snapshot has no matching failed application.", nameof(batchId));
        DataGenerationDescriptor? generation = null;
        Exception? notificationFailure = null;
        SettingsAuthorityResult result = await ExecuteConsumerAsync(async (context, lease, token) =>
        {
            generation = context.Session.Generation;
            SettingsEnvelope current = context.Session.Snapshot;
            if (!generation.IsSameGeneration(expected.Generation) || !current.PendingApplications.Any(batch =>
                batch.BatchId == selected.BatchId && batch.AttemptId == selected.AttemptId
                && batch.State == SettingsApplicationBatchState.Failed && batch.ApplicationKind == selected.ApplicationKind
                && batch.Entries.SequenceEqual(selected.Entries)))
            {
                return new(SettingsAuthorityStatus.Rejected, current, "settings.recovery.stale_attempt");
            }
            SettingsAuthorityResult retried = await context.Session.RetryAdmittedAsync(
                selected.BatchId, selected.AttemptId, Guid.NewGuid(), lease, token).ConfigureAwait(false);
            if (!retried.IsSucceeded) { return retried; }
            SettingsApplicationBatch pending = retried.Envelope!.PendingApplications.Single(batch => batch.BatchId == selected.BatchId);
            return await ApplyOneAsync(context, retried.Envelope, pending, SettingsApplicationPhase.Live, lease).ConfigureAwait(false);
        }, drainProducers: true, onPublicationFailure: failure => notificationFailure = failure, cancellationToken).ConfigureAwait(false);
        return new(generation!, result, notificationFailure);
    }
}
