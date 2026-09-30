using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

internal sealed record GenerationRuntimePreparationResult(bool IsSucceeded, bool RequiresRestart, string? Code);

internal sealed partial class AppDataGenerationRuntime
{
    /// <summary>Verifies replacement settings without replaying outbox actions or releasing queued trigger events.</summary>
    public async Task<GenerationRuntimePreparationResult> PrepareReplacementAdmittedAsync(
        MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        admission.EnsureActiveExclusiveLease(lease);
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started || publication.IsPublished) { throw new InvalidOperationException("An active runtime cannot be prepared as a replacement."); }
            if (_replacementPrepared)
            {
                return new(true, Repositories.Session.Snapshot.PendingApplications.Any(batch => batch.Kind == SettingsApplicationBatchKind.Restart), null);
            }
            // The candidate planner has already queued fresh observations. Startup preparation
            // would convert restart-bound work into startup reconciliation and apply it live.
            var definitions = await Definitions.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (!definitions.IsSucceeded) { return new(false, false, definitions.Diagnostic?.Code ?? "trigger.candidate.catalog_unavailable"); }
            await TriggerSettings.Scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
            var context = (SettingsGenerationContext)Repositories.GetService(typeof(SettingsGenerationContext))!;
            SettingApplicationKind[] order = [SettingApplicationKind.Internal, SettingApplicationKind.Appearance,
                SettingApplicationKind.StartupTask, SettingApplicationKind.Network, SettingApplicationKind.Sampling, SettingApplicationKind.Triggers];
            bool restart = false;
            foreach (SettingApplicationKind kind in order)
            {
                foreach (SettingsApplicationBatch batch in Repositories.Session.Snapshot.PendingApplications.Where(batch => batch.ApplicationKind == kind).ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    SettingsAuthorityResult applied = await Repositories.Session.ApplyBatchAdmittedAsync(
                        batch.BatchId, batch.AttemptId, context.Participants[kind], SettingsApplicationPhase.Live, lease, cancellationToken).ConfigureAwait(false);
                    if (applied.Status == SettingsAuthorityStatus.DeferredToRestart) { restart = true; continue; }
                    if (!applied.IsSucceeded) { return new(false, restart, applied.Code); }
                }
            }
            _replacementPrepared = true;
            return new(true, restart, null);
        }
        finally { _startupGate.Release(); }
    }

    /// <summary>Starts committed background work only after the transition owner has reopened ordinary mutation admission.</summary>
    public Task PublishCommittedAsync(CancellationToken cancellationToken) =>
        generations.ExecuteAsync<AppDataGenerationRuntime>((current, _, token) =>
        {
            if (!ReferenceEquals(current, this)) { throw new InvalidOperationException("Only the current committed runtime can publish background work."); }
            return PublishCommittedCoreAsync(token);
        }, cancellationToken);

    private async Task PublishCommittedCoreAsync(CancellationToken cancellationToken)
    {
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_replacementPrepared) { throw new InvalidOperationException("Only a prepared replacement can publish its background work."); }
            if (publication.IsPublished) { return; }
            if (admission.State != MutationAdmissionState.Open) { throw new InvalidOperationException("Mutation admission is still closed."); }
            await Subscriptions.StartAsync(cancellationToken).ConfigureAwait(false);
            PublishTriggerProcessing();
            _started = true;
        }
        finally { _startupGate.Release(); }
    }
}
