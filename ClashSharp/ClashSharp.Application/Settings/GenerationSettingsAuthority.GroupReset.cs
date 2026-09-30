using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

public sealed partial class GenerationSettingsAuthority : ISettingsRuntimeGroupReset
{
    /// <inheritdoc />
    public async Task<SettingsRuntimeGroupResetResult> ResetRuntimeGroupAsync(
        SettingsResetScope scope, bool canUseTransparentProxy, CancellationToken cancellationToken)
    {
        SettingValueChange[] defaults = RuntimeGroupDefaults(scope, canUseTransparentProxy);
        DataGenerationDescriptor? generation = null;
        Exception? notificationFailure = null;
        SettingsAuthorityResult outcome = await ExecuteConsumerAsync(async (context, lease, token) =>
        {
            generation = context.Session.Generation;
            SettingsAuthorityResult result = await ChangeAndObserveAsync(context, defaults, Guid.NewGuid(), lease, token).ConfigureAwait(false);
            // A retained failed attempt cannot be begun again. Present it as unfinished application
            // so the user can explicitly retry, without silently allocating a replacement attempt.
            HashSet<SettingKey> keys = defaults.Select(change => change.Key).ToHashSet();
            return result.Status == SettingsAuthorityStatus.Rejected && result.Code == "settings.application.state"
                && result.Envelope!.PendingApplications.Any(batch => batch.State == SettingsApplicationBatchState.Failed
                    && batch.Entries.Any(entry => keys.Contains(entry.Key)))
                ? new(SettingsAuthorityStatus.ApplicationFailed, result.Envelope, "settings.reset.retry_required") : result;
        }, drainProducers: true, onPublicationFailure: failure => notificationFailure = failure, cancellationToken).ConfigureAwait(false);
        return new(generation!, scope, outcome, notificationFailure);
    }

    /// <inheritdoc />
    public async Task<SettingsRuntimeGroupResetResult> RetryRuntimeGroupAsync(
        SettingsRuntimeGroupResetResult previous, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(previous);
        SettingKey[] keys = RuntimeGroupDefaults(previous.Scope, canUseTransparentProxy: false).Select(change => change.Key).ToArray();
        SettingsApplicationBatch[] expected = previous.FailedBatches.ToArray();
        if (!previous.CanRetry || expected.Length == 0) { throw new ArgumentException("The result has no failed group applications.", nameof(previous)); }

        DataGenerationDescriptor? generation = null;
        Exception? notificationFailure = null;
        SettingsAuthorityResult outcome = await ExecuteConsumerAsync(async (context, lease, token) =>
        {
            generation = context.Session.Generation;
            SettingsEnvelope envelope = context.Session.Snapshot;
            if (!generation.IsSameGeneration(previous.Generation) || expected.Any(batch =>
                !envelope.PendingApplications.Any(current => current.BatchId == batch.BatchId
                    && current.AttemptId == batch.AttemptId && current.State == SettingsApplicationBatchState.Failed
                    && current.ApplicationKind == batch.ApplicationKind && current.Entries.SequenceEqual(batch.Entries))))
            {
                return new(SettingsAuthorityStatus.Rejected, envelope, "settings.reset.stale_retry");
            }

            SettingsAuthorityResult result = new(SettingsAuthorityStatus.NoChange, envelope);
            bool accepted = false;
            foreach (SettingsApplicationBatch failed in expected)
            {
                result = accepted
                    ? await context.Session.ContinueRetryAdmittedAsync(failed.BatchId, failed.AttemptId, Guid.NewGuid(), lease).ConfigureAwait(false)
                    : await context.Session.RetryAdmittedAsync(failed.BatchId, failed.AttemptId, Guid.NewGuid(), lease, token).ConfigureAwait(false);
                if (!result.IsSucceeded) { return result; }
                // A persisted retry is now owned by this command, including later participants and cancellation.
                token = CancellationToken.None;
                accepted = true;
                SettingsApplicationBatch retry = result.Envelope!.PendingApplications.Single(batch => batch.BatchId == failed.BatchId);
                result = await ApplyOneAsync(context, result.Envelope, retry, SettingsApplicationPhase.Live, lease).ConfigureAwait(false);
                if (!result.IsSucceeded) { return result; }
            }

            // The original command may have stopped before reaching other affected pending participants.
            result = await ApplyAffectedAsync(context, result, keys.ToHashSet(), SettingsApplicationPhase.Live, lease).ConfigureAwait(false);
            return !result.IsSucceeded || keys.All(key => result.Envelope!.Applied[key].Kind == SettingAppliedStateKind.Verified
                    && result.Envelope.Applied[key].Value!.Equals(result.Envelope.Desired[key].Value))
                ? result : new(SettingsAuthorityStatus.ApplicationFailed, result.Envelope, "settings.application.unverified_runtime_effect");
        }, drainProducers: true, onPublicationFailure: failure => notificationFailure = failure, cancellationToken).ConfigureAwait(false);
        return new(generation!, previous.Scope, outcome, notificationFailure);
    }

    private static SettingValueChange[] RuntimeGroupDefaults(SettingsResetScope scope, bool canUseTransparentProxy)
    {
        if (scope is not (SettingsResetScope.Startup or SettingsResetScope.Proxy or SettingsResetScope.TransparentProxy))
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }
        return SettingsRegistry.Default.GetResetDefinitions(scope).Select(definition => new SettingValueChange(definition.Key,
            definition.Key == SettingsRegistry.Keys.TransparentProxyEnabled && !canUseTransparentProxy
                ? definition.NormalizeValue(false).Value! : definition.DefaultValue)).ToArray();
    }
}
