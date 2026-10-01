using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Hosting.Settings;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

internal sealed record GenerationReplacementResult(DataGenerationDescriptor Generation, bool RequiresRestart, IReadOnlyList<string> Warnings);

internal sealed class GenerationReplacementRecoveryException(Exception operationFailure, Exception recoveryFailure, bool committed)
    : AggregateException("The data transaction requires recovery before ordinary changes can continue.", operationFailure, recoveryFailure)
{
    public bool IsCommitted { get; } = committed;
}

/// <summary>Owns in-process replacement, native compensation and producer publication through the complete exclusive decision.</summary>
/// <remarks>Crash recovery and page integration must use the same durable manifest decision before enabling this entry point in the UI.</remarks>
internal sealed class GenerationReplacementCoordinator(DataGenerationManager generations, MutationAdmissionBarrier admission,
    IDataGenerationStore store, GenerationDataCandidatePreparer preparer, GenerationSettingsAuthority authority,
    IGenerationReplacementJournal journal)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public Task<GenerationReplacementResult> ImportAsync(string packagePath, CancellationToken cancellationToken) => ExecuteAsync(packagePath, cancellationToken);
    public Task<GenerationReplacementResult> ResetAllSettingsAsync(CancellationToken cancellationToken) => ExecuteAsync(null, cancellationToken);

    private async Task<GenerationReplacementResult> ExecuteAsync(string? packagePath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        MutationAdmissionLease? lease = null;
        DataGenerationTransition? transition = null;
        DataGenerationManifestSnapshot? baseline = null;
        GenerationExternalStateSnapshot? external = null;
        List<(string Name, QuiescedState State)> paused = [];
        bool publicationWasOpen = false;
        bool nativeEffectsStarted = false;
        bool retained = false;
        bool leaseReleased = false;
        List<string> warnings = [];
        GenerationRuntimePreparationResult? prepared = null;
        Guid operationId = Guid.NewGuid();
        bool checkpointAttempted = false;
        bool checkpointReady = false;
        string? quiescingProducer = null;
        try
        {
            lease = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false);
            baseline = generations.CurrentManifest;
            try
            {
                if (await journal.ReadPendingAsync(cancellationToken).ConfigureAwait(false) is not null)
                {
                    throw new InvalidOperationException("A previous data replacement requires startup recovery.");
                }
            }
            catch (Exception pendingFailure) when (!ExceptionGraphClassifier.IsProcessFatal(pendingFailure)
                && !ExceptionGraphClassifier.IsCallerCancellation(pendingFailure, cancellationToken))
            {
                retained = true;
                await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false);
                throw new GenerationReplacementRecoveryException(pendingFailure,
                    new InvalidOperationException("The replacement checkpoint could not be cleared for a new operation."), committed: false);
            }
            DataPackageImportPlan? plan = packagePath is null ? null : await generations.ExecuteAsync<AppDataGenerationRuntime, DataPackageImportPlan>(
                (runtime, descriptor, token) => preparer.ReadImportAdmittedAsync(packagePath, runtime.Repositories.Session.Snapshot, descriptor, lease, token),
                cancellationToken).ConfigureAwait(false);
            await generations.ExecuteAsync<AppDataGenerationRuntime>(async (runtime, descriptor, token) =>
            {
                if (!descriptor.IsSameGeneration(baseline.Descriptor) || !runtime.IsExecutionPublished)
                {
                    throw new InvalidOperationException("The baseline runtime is not ready for a data transaction.");
                }
                external = await runtime.ExternalState.CaptureAdmittedAsync(lease, token).ConfigureAwait(false);
                checkpointAttempted = true;
                await journal.BeginAsync(operationId, baseline, external, token).ConfigureAwait(false);
                checkpointReady = true;
                publicationWasOpen = runtime.HoldExecutionAdmitted(lease);
                foreach (IRuntimeParticipant producer in Producers(runtime))
                {
                    quiescingProducer = producer.Name;
                    paused.Add((producer.Name, await producer.QuiesceAsync(CancellationToken.None).ConfigureAwait(false)));
                    quiescingProducer = null;
                }
                token.ThrowIfCancellationRequested();
                nativeEffectsStarted = true;
                // Preserve user intent while releasing the old namespace and accounting for its last traffic sample.
                await runtime.Network.RestoreConfigurationAsync(new NetworkSettingsConfiguration(
                    ClashSharpMode.Disabled, "builtin-direct", false, external.Network.MixedPort), CancellationToken.None).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            // Native release has started. Finish the decision and any compensation even if the page closes.
            transition = await generations.BeginDrainAsync(baseline.ContentHash, CancellationToken.None).ConfigureAwait(false);
            if (plan is null) { _ = await preparer.StageResetAdmittedAsync(transition, lease, CancellationToken.None).ConfigureAwait(false); }
            else { _ = await preparer.StagePlanAdmittedAsync(transition, plan, lease, CancellationToken.None).ConfigureAwait(false); }
            await journal.SetCandidateAsync(operationId, transition.StagedDescriptor!, CancellationToken.None).ConfigureAwait(false);
            prepared = await transition.ExecuteCandidateAsync<AppDataGenerationRuntime, GenerationRuntimePreparationResult>(
                (runtime, _, token) => runtime.PrepareReplacementAdmittedAsync(lease, token), CancellationToken.None).ConfigureAwait(false);
            if (!prepared.IsSucceeded) { throw new InvalidOperationException(prepared.Code ?? "Candidate runtime verification failed."); }
            try { _ = await transition.PromoteManifestAsync(store, CancellationToken.None, retainCandidateOnFailure: true).ConfigureAwait(false); }
            catch (DataGenerationManagerException failure) when (failure.Error == DataGenerationManagerError.ManifestPromotionCommitted && transition.IsManifestPromoted)
            {
                warnings.Add("data.replacement.promotion_reply_lost");
            }
            transition.SwapToPromoted();
            await CompleteWithRetryAsync(() => transition.CommitAsync().AsTask()).ConfigureAwait(false);
            await journal.CompleteAsync(operationId, CancellationToken.None).ConfigureAwait(false);
            try { _ = await authority.PublishCurrentAdmittedAsync(lease, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { warnings.Add("data.replacement.notification_failed"); }
            await lease.DisposeAsync().ConfigureAwait(false);
            leaseReleased = true;
            try
            {
                await generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, token) => runtime.PublishCommittedAsync(token), CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)) { warnings.Add("data.replacement.background_start_failed"); }
            bool restart = prepared.RequiresRestart || warnings.Any(code => code is "data.replacement.notification_failed" or "data.replacement.background_start_failed");
            return new(transition.StagedDescriptor!, restart, warnings.AsReadOnly());
        }
        catch (Exception fatal) when (ExceptionGraphClassifier.IsProcessFatal(fatal))
        {
            retained = true;
            if (lease is not null)
            {
                try { await RetainAdmissionAsync(lease, leaseReleased).ConfigureAwait(false); }
                catch (Exception retentionFailure) { throw new AggregateException(fatal, retentionFailure); }
            }
            throw;
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            if (retained) { throw; }
            if (lease is null) { throw; }
            if (quiescingProducer is not null)
            {
                retained = true;
                await RetainAdmissionAsync(lease, leaseReleased).ConfigureAwait(false);
                throw new GenerationReplacementRecoveryException(failure,
                    new InvalidOperationException($"Producer '{quiescingProducer}' did not return a verified quiesced state."), committed: false);
            }
            if (transition?.IsCommitted == true || transition?.IsManifestPromoted == true
                || failure is DataGenerationManagerException { Error: DataGenerationManagerError.ManifestPromotionUncertain })
            {
                retained = true;
                await RetainAdmissionAsync(lease, leaseReleased).ConfigureAwait(false);
                throw new GenerationReplacementRecoveryException(failure, new InvalidOperationException("The durable directory decision requires startup recovery."), committed: transition?.IsManifestPromoted == true);
            }
            try
            {
                if (transition?.StagedDescriptor is not null)
                {
                    _ = await transition.ExecuteCandidateAsync<AppDataGenerationRuntime, bool>(async (runtime, _, token) =>
                    {
                        List<Exception> stopFailures = [];
                        foreach (IRuntimeParticipant producer in Producers(runtime).Reverse())
                        {
                            try { await producer.StopAsync(token).ConfigureAwait(false); }
                            catch (Exception stopFailure) when (!ExceptionGraphClassifier.IsProcessFatal(stopFailure)) { stopFailures.Add(stopFailure); }
                        }
                        if (stopFailures.Count > 0) { throw new AggregateException(stopFailures); }
                        return true;
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                if (nativeEffectsStarted && external is not null)
                {
                    if (transition is null)
                    {
                        await generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, token) => runtime.ExternalState.RestoreAdmittedAsync(external, lease, token), CancellationToken.None).ConfigureAwait(false);
                    }
                    else
                    {
                        _ = await transition.ExecuteBaselineAsync<AppDataGenerationRuntime, bool>(async (runtime, _, token) =>
                        {
                            await runtime.ExternalState.RestoreAdmittedAsync(external, lease, token).ConfigureAwait(false);
                            return true;
                        }, CancellationToken.None).ConfigureAwait(false);
                    }
                }
                if (transition is not null)
                {
                    if (transition.IsManifestPromoted) { _ = await transition.RestoreBaselineAsync(store, CancellationToken.None).ConfigureAwait(false); }
                    else { await transition.AbortAsync(store, CancellationToken.None).ConfigureAwait(false); }
                }
                if (checkpointAttempted)
                {
                    if (checkpointReady) { await journal.CompleteAsync(operationId, CancellationToken.None).ConfigureAwait(false); }
                    else
                    {
                        GenerationReplacementCheckpoint? pending = await journal.ReadPendingAsync(CancellationToken.None).ConfigureAwait(false);
                        if (pending is not null)
                        {
                            if (pending.OperationId != operationId) { throw new InvalidOperationException("Another replacement owns the recovery checkpoint."); }
                            await journal.CompleteAsync(operationId, CancellationToken.None).ConfigureAwait(false);
                        }
                    }
                }
                if (paused.Count > 0 || publicationWasOpen)
                {
                    await generations.ExecuteAsync<AppDataGenerationRuntime>(async (runtime, descriptor, token) =>
                    {
                        if (!descriptor.IsSameGeneration(baseline!.Descriptor)) { throw new InvalidOperationException("The rollback runtime changed before resume."); }
                        foreach (var state in paused.AsEnumerable().Reverse())
                        {
                            await Producers(runtime).Single(producer => producer.Name == state.Name).ResumeAsync(state.State, token).ConfigureAwait(false);
                        }
                        // Settings participants must be ready before another ordinary command can
                        // enter. Keep this generation pinned through reopening and publication.
                        await lease.DisposeAsync().ConfigureAwait(false);
                        leaseReleased = true;
                        runtime.RestoreExecutionPublication(publicationWasOpen);
                    }, CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await lease.DisposeAsync().ConfigureAwait(false);
                    leaseReleased = true;
                }
            }
            catch (Exception recoveryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(recoveryFailure))
            {
                retained = true;
                try { await RetainAdmissionAsync(lease, leaseReleased).ConfigureAwait(false); }
                catch (Exception retentionFailure) when (!ExceptionGraphClassifier.IsProcessFatal(retentionFailure))
                {
                    throw new GenerationReplacementRecoveryException(failure,
                        new AggregateException(recoveryFailure, retentionFailure), committed: false);
                }
                throw new GenerationReplacementRecoveryException(failure, recoveryFailure, committed: false);
            }
            ExceptionDispatchInfo.Capture(failure).Throw();
            throw;
        }
        finally
        {
            try
            {
                if (!retained && transition is not null) { await transition.DisposeAsync().ConfigureAwait(false); }
                if (lease is not null) { await lease.DisposeAsync().ConfigureAwait(false); }
            }
            finally { _gate.Release(); }
        }
    }

    private static IRuntimeParticipant[] Producers(AppDataGenerationRuntime runtime)
    {
        IRuntimeParticipant[] primary = [runtime.TriggerSettings.Scheduler, runtime.Subscriptions, runtime.Sampling];
        return [.. primary, .. runtime.Repositories.CaptureProducers().Where(
            producer => !primary.Any(existing => ReferenceEquals(existing, producer)))];
    }

    private async Task RetainAdmissionAsync(MutationAdmissionLease lease, bool released)
    {
        if (!released)
        {
            await lease.RetainRecoveryOnlyAsync().ConfigureAwait(false);
            return;
        }
        if (admission.State is MutationAdmissionState.RecoveryOnly or MutationAdmissionState.ClosedForShutdown) { return; }
        using MutationAdmissionLease closure = await admission.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None).ConfigureAwait(false);
        await closure.RetainRecoveryOnlyAsync().ConfigureAwait(false);
    }

    private static async Task CompleteWithRetryAsync(Func<Task> completion)
    {
        try { await completion().ConfigureAwait(false); }
        catch (Exception first) when (!ExceptionGraphClassifier.IsProcessFatal(first))
        {
            try { await completion().ConfigureAwait(false); }
            catch (Exception retry) when (!ExceptionGraphClassifier.IsProcessFatal(retry)) { throw new AggregateException(first, retry); }
        }
    }
}
