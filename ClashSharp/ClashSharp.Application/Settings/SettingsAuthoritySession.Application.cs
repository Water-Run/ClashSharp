using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

public sealed partial class SettingsAuthoritySession
{
    /// <summary>Persists running intent, probes before applying, and clears work only after complete independent verification.</summary>
    /// <param name="batchId">Exact batch selected from the verified envelope.</param>
    /// <param name="attemptId">Expected current attempt identity.</param>
    /// <param name="participant">Admitted runtime implementation for this batch's application kind.</param>
    /// <param name="phase">Live application or startup under exclusive ownership.</param>
    /// <param name="admissionLease">Active lease retained through observation, effects, and final durable classification.</param>
    /// <param name="cancellationToken">Cancels waiting and work before running intent is durably acknowledged.</param>
    public Task<SettingsAuthorityResult> ApplyBatchAdmittedAsync(
        Guid batchId, Guid attemptId, ISettingsApplicationParticipant participant, SettingsApplicationPhase phase,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken) =>
        ApplyBatchEntryAsync(batchId, attemptId, participant, phase, admissionLease, honorRevocation: true, cancellationToken);

    /// <summary>Continues a facade-owned durable command while retaining its active lease across admission drain.</summary>
    internal Task<SettingsAuthorityResult> ContinueCommittedBatchAdmittedAsync(
        Guid batchId, Guid attemptId, ISettingsApplicationParticipant participant, SettingsApplicationPhase phase,
        MutationAdmissionLease admissionLease) =>
        ApplyBatchEntryAsync(batchId, attemptId, participant, phase, admissionLease, honorRevocation: false, CancellationToken.None);

    private Task<SettingsAuthorityResult> ApplyBatchEntryAsync(
        Guid batchId, Guid attemptId, ISettingsApplicationParticipant participant, SettingsApplicationPhase phase,
        MutationAdmissionLease admissionLease, bool honorRevocation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(participant);
        if (!Enum.IsDefined(phase)) { throw new ArgumentOutOfRangeException(nameof(phase)); }
        if (phase == SettingsApplicationPhase.Startup) { _admission.EnsureActiveExclusiveLease(admissionLease); }
        return ExecuteAdmittedAsync(admissionLease,
            waiting => ApplyCoreAsync(batchId, attemptId, participant, phase, admissionLease, waiting), honorRevocation, cancellationToken);
    }

    private async Task<SettingsAuthorityResult> ApplyCoreAsync(
        Guid batchId, Guid attemptId, ISettingsApplicationParticipant participant, SettingsApplicationPhase phase,
        MutationAdmissionLease lease, CancellationToken waiting)
    {
        SettingsAuthorityResult read = await ReadCoreAsync(waiting).ConfigureAwait(false);
        if (!read.IsSucceeded) { return read; }
        SettingsEnvelope envelope = read.Envelope!;
        SettingsApplicationBatch? batch = envelope.PendingApplications.SingleOrDefault(item => item.BatchId == batchId);
        if (batch is null || batch.AttemptId != attemptId)
        {
            return new(SettingsAuthorityStatus.Rejected, envelope, "settings.application.stale_attempt");
        }

        if (batch.ApplicationKind != participant.ApplicationKind)
        {
            return new(SettingsAuthorityStatus.Rejected, envelope, "settings.application.participant_mismatch");
        }

        if (batch.Kind == SettingsApplicationBatchKind.Restart && phase != SettingsApplicationPhase.Startup)
        {
            return new(SettingsAuthorityStatus.DeferredToRestart, envelope, "settings.application.restart_required");
        }

        waiting.ThrowIfCancellationRequested();
        SettingsAuthorityResult started = await PersistEditAsync(envelope,
            _batches.BeginAttempt(envelope, batchId, attemptId), waiting).ConfigureAwait(false);
        if (!started.IsSucceeded) { return started; }
        envelope = started.Envelope!;
        batch = envelope.PendingApplications.Single(item => item.BatchId == batchId);
        SettingsApplicationRequest request = new(Generation, envelope, batch, phase);

        // Running intent is durable. Ignore page cancellation until the participant and
        // the final save finish; exclusive generation changes still wait for this lease.
        SettingsApplicationObservation? observation = await TryProbeAsync(participant, request, lease).ConfigureAwait(false);
        if (!ValidateObservation(request, observation, out bool matches))
        {
            return await FailCoreAsync(envelope, batch, "settings.application.probe_failed").ConfigureAwait(false);
        }

        bool applied = false;
        bool replyFailed = false;
        if (!matches)
        {
            try
            {
                await participant.ApplyAsync(request, lease, CancellationToken.None).ConfigureAwait(false);
                applied = true;
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                replyFailed = true;
                // A participant may have completed its effect before losing the reply.
                // Resolve that ambiguity by probing; never infer rollback from an exception.
            }

            observation = await TryProbeAsync(participant, request, lease).ConfigureAwait(false);
            if (!ValidateObservation(request, observation, out matches) || !matches)
            {
                return await FailCoreAsync(envelope, batch, replyFailed
                    ? "settings.application.participant_failed" : "settings.application.verification_failed").ConfigureAwait(false);
            }
        }

        SettingAppliedValueSource source = phase == SettingsApplicationPhase.Startup
            ? SettingAppliedValueSource.StartupReconciliation
            : applied ? SettingAppliedValueSource.MutationVerification : SettingAppliedValueSource.RuntimeProbe;
        SettingsEnvelopeEditResult complete = _batches.CompleteAttempt(envelope, batchId, attemptId,
            observation!.Values, source, _time.GetUtcNow());
        SettingsAuthorityResult completed = await PersistEditAsync(envelope, complete, CancellationToken.None).ConfigureAwait(false);
        return completed.IsSucceeded && replyFailed
            ? new(completed.Status, completed.Envelope, "settings.application.reply_lost_resolved") : completed;
    }

    private async Task<SettingsAuthorityResult> FailCoreAsync(SettingsEnvelope source, SettingsApplicationBatch batch, string code)
    {
        SettingsAuthorityResult failed = await PersistEditAsync(source,
            _batches.FailAttempt(source, batch.BatchId, batch.AttemptId, new(code)), CancellationToken.None).ConfigureAwait(false);
        return failed.IsSucceeded ? new(SettingsAuthorityStatus.ApplicationFailed, failed.Envelope, code) : failed;
    }

    private static async Task<SettingsApplicationObservation?> TryProbeAsync(
        ISettingsApplicationParticipant participant, SettingsApplicationRequest request, MutationAdmissionLease lease)
    {
        try
        {
            return await participant.ProbeAsync(request, lease, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return null;
        }
    }

    private bool ValidateObservation(SettingsApplicationRequest request, SettingsApplicationObservation? observation, out bool matches)
    {
        matches = false;
        if (observation is null || !Generation.IsSameGeneration(observation.Generation)
            || observation.BatchId != request.Batch.BatchId || observation.AttemptId != request.Batch.AttemptId
            || observation.Values.Count != request.Values.Count) { return false; }
        HashSet<SettingKey> seen = [];
        bool allMatch = true;
        foreach (SettingValueChange value in observation.Values)
        {
            if (value is null || !seen.Add(value.Key) || !request.Values.TryGetValue(value.Key, out SettingValue? desired)) { return false; }
            SettingNormalizationResult normalized = _registry.Get(value.Key.Value).Normalize(value.Value.CanonicalText);
            if (!normalized.IsSuccess || !value.Value.Equals(normalized.Value)) { return false; }
            allMatch &= value.Value.Equals(desired);
        }

        matches = allMatch;
        return true;
    }
}
