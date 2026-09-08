using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Owns admission and one generation pin across complete consumer commands, including every affected runtime batch.</summary>
public sealed class GenerationSettingsAuthority : ISettingsAuthority
{
    private readonly DataGenerationManager _generations;
    private readonly MutationAdmissionBarrier _admission;
    private readonly SemaphoreSlim _commandGate = new(1, 1);

    /// <summary>Creates the facade without resolving services, opening data, or publishing defaults.</summary>
    /// <param name="generations">Owner of current repositories and transition drain.</param>
    /// <param name="admission">Shared mutation authority for ordinary and exclusive work.</param>
    public GenerationSettingsAuthority(DataGenerationManager generations, MutationAdmissionBarrier admission)
    {
        _generations = generations ?? throw new ArgumentNullException(nameof(generations));
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
    }

    /// <inheritdoc />
    public SettingsAuthoritySnapshot CaptureSnapshot() => _generations.ReadSnapshot<SettingsGenerationContext, SettingsAuthoritySnapshot>(
        (context, generation) => new(generation, RequireSession(context, generation).Snapshot));

    /// <inheritdoc />
    public Task<SettingsAuthorityResult> OpenAsync(CancellationToken cancellationToken) =>
        ExecuteOrdinaryAsync((context, lease, token) => context.Session.OpenAdmittedAsync(lease, token), cancellationToken);

    /// <inheritdoc />
    public Task<SettingsAuthorityResult> ApplyChangesAsync(
        IEnumerable<SettingValueChange> changes, Guid transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        return ExecuteOrdinaryAsync((context, lease, token) => ChangeAndApplyAsync(context, snapshot, transactionId, lease, token), cancellationToken);
    }

    /// <inheritdoc />
    public Task<SettingsAuthorityResult> ApplyChangesAdmittedAsync(
        IEnumerable<SettingValueChange> changes, Guid transactionId, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(changes);
        SettingValueChange[] snapshot = changes.ToArray();
        return ExecuteAdmittedAsync((context, lease, token) => ChangeAndApplyAsync(context, snapshot, transactionId, lease, token),
            admissionLease, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SettingsAuthorityResult> RevertAsync(IEnumerable<SettingKey> keys, Guid transactionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(keys);
        SettingKey[] snapshot = keys.ToArray();
        return ExecuteOrdinaryAsync(async (context, lease, token) =>
        {
            SettingsAuthorityResult reverted = await context.Session.RevertAdmittedAsync(snapshot, transactionId, lease, token).ConfigureAwait(false);
            return await ApplyAffectedAsync(context, reverted, snapshot.ToHashSet(), SettingsApplicationPhase.Live, lease).ConfigureAwait(false);
        }, cancellationToken);
    }

    /// <inheritdoc />
    public Task<SettingsAuthorityResult> RetryAsync(Guid batchId, Guid expectedAttemptId, Guid newAttemptId, CancellationToken cancellationToken) =>
        ExecuteOrdinaryAsync(async (context, lease, token) =>
        {
            SettingsAuthorityResult retry = await context.Session.RetryAdmittedAsync(batchId, expectedAttemptId, newAttemptId, lease, token).ConfigureAwait(false);
            if (!retry.IsSucceeded) { return retry; }
            SettingsApplicationBatch batch = retry.Envelope!.PendingApplications.Single(item => item.BatchId == batchId);
            return await ApplyOneAsync(context, retry.Envelope, batch, SettingsApplicationPhase.Live, lease).ConfigureAwait(false);
        }, cancellationToken);

    /// <inheritdoc />
    public Task<SettingsAuthorityResult> ReconcileStartupAdmittedAsync(
        Guid startupId, MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveExclusiveLease(admissionLease);
        return ExecuteAdmittedAsync(async (context, lease, token) =>
        {
            SettingsAuthorityResult prepared = await context.Session.PrepareStartupAdmittedAsync(startupId, lease, token).ConfigureAwait(false);
            return !prepared.IsSucceeded ? prepared : await ApplyAffectedAsync(context, prepared,
                prepared.Envelope!.Desired.Keys.ToHashSet(), SettingsApplicationPhase.Startup, lease).ConfigureAwait(false);
        }, admissionLease, cancellationToken);
    }

    private static async Task<SettingsAuthorityResult> ChangeAndApplyAsync(
        SettingsGenerationContext context, SettingValueChange[] changes, Guid transactionId,
        MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        SettingsAuthorityResult changed = await context.Session.ChangeAdmittedAsync(changes, transactionId, lease, cancellationToken).ConfigureAwait(false);
        return !changed.IsSucceeded ? changed : await ApplyAffectedAsync(context, changed,
            changes.Select(change => change.Key).ToHashSet(), SettingsApplicationPhase.Live, lease).ConfigureAwait(false);
    }

    private static async Task<SettingsAuthorityResult> ApplyAffectedAsync(
        SettingsGenerationContext context, SettingsAuthorityResult committed, IReadOnlySet<SettingKey> keys,
        SettingsApplicationPhase phase, MutationAdmissionLease lease)
    {
        if (!committed.IsSucceeded) { return committed; }
        SettingsApplicationBatch[] batches = committed.Envelope!.PendingApplications
            .Where(batch => batch.Entries.Any(entry => keys.Contains(entry.Key))).ToArray();
        SettingsAuthorityResult result = committed;
        bool deferred = false;
        foreach (SettingsApplicationBatch batch in batches)
        {
            SettingsAuthorityResult applied = await ApplyOneAsync(context, result.Envelope!, batch, phase, lease).ConfigureAwait(false);
            if (applied.Status == SettingsAuthorityStatus.DeferredToRestart) { deferred = true; result = applied; continue; }
            if (!applied.IsSucceeded) { return applied; }
            result = applied;
        }

        return deferred ? new(SettingsAuthorityStatus.DeferredToRestart, result.Envelope, "settings.application.restart_required") : result;
    }

    private static Task<SettingsAuthorityResult> ApplyOneAsync(
        SettingsGenerationContext context, SettingsEnvelope envelope, SettingsApplicationBatch batch,
        SettingsApplicationPhase phase, MutationAdmissionLease lease)
    {
        if (!context.Participants.TryGetValue(batch.ApplicationKind, out ISettingsApplicationParticipant? participant))
        {
            return Task.FromResult(new SettingsAuthorityResult(SettingsAuthorityStatus.Rejected, envelope, "settings.application.participant_missing"));
        }

        return context.Session.ContinueCommittedBatchAdmittedAsync(batch.BatchId, batch.AttemptId, participant, phase, lease);
    }

    private async Task<SettingsAuthorityResult> ExecuteOrdinaryAsync(
        Func<SettingsGenerationContext, MutationAdmissionLease, CancellationToken, Task<SettingsAuthorityResult>> command,
        CancellationToken cancellationToken)
    {
        await using MutationAdmissionLease lease = await _admission.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
        return await ExecuteAdmittedAsync(command, lease, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SettingsAuthorityResult> ExecuteAdmittedAsync(
        Func<SettingsGenerationContext, MutationAdmissionLease, CancellationToken, Task<SettingsAuthorityResult>> command,
        MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        _admission.EnsureActiveLease(lease);
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RevocationToken);
        await _commandGate.WaitAsync(waiting.Token).ConfigureAwait(false);
        try
        {
            _admission.EnsureActiveLease(lease);
            waiting.Token.ThrowIfCancellationRequested();
            return await _generations.ExecuteAsync<SettingsGenerationContext, SettingsAuthorityResult>((context, generation, token) =>
            {
                _ = RequireSession(context, generation);
                return command(context, lease, token);
            }, waiting.Token).ConfigureAwait(false);
        }
        finally
        {
            _commandGate.Release();
        }
    }

    private static SettingsAuthoritySession RequireSession(SettingsGenerationContext context, DataGenerationDescriptor generation) =>
        context.Session.Generation.IsSameGeneration(generation) ? context.Session
            : throw new InvalidOperationException("The resolved settings session does not belong to the pinned generation.");
}
