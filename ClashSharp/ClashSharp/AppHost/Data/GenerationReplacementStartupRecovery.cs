using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Hosting.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Classifies an interrupted replacement from the verified durable pointer before startup opens runtime execution.</summary>
internal sealed class GenerationReplacementStartupRecovery(IGenerationReplacementJournal journal,
    IDataGenerationStore store, DataGenerationManager generations, MutationAdmissionBarrier admission)
{
    public async Task ValidateBeforeBootstrapAdmittedAsync(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        admission.EnsureActiveExclusiveLease(lease);
        GenerationReplacementCheckpoint? pending = await journal.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending is null) { return; }
        DataGenerationManifestSnapshot current = await store.LoadCurrentAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("A pending replacement cannot initialize a missing current-directory pointer.");
        _ = IsCommittedDecision(pending, current);
    }

    public async Task<GenerationReplacementCheckpoint?> PrepareAdmittedAsync(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        admission.EnsureActiveExclusiveLease(lease);
        GenerationReplacementCheckpoint? pending = await journal.ReadPendingAsync(cancellationToken).ConfigureAwait(false);
        if (pending is null) { return null; }
        DataGenerationManifestSnapshot current = await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        bool committed = IsCommittedDecision(pending, current);
        await generations.ExecuteAsync<AppDataGenerationRuntime>(async (runtime, descriptor, token) =>
        {
            if (!descriptor.IsSameGeneration(current.Descriptor)) { throw new InvalidOperationException("The startup recovery directory changed."); }
            if (!committed)
            {
                await runtime.ExternalState.RestoreAdmittedAsync(pending.External, lease, token).ConfigureAwait(false);
            }
            else
            {
                // A service child may have survived the previous App process. Establish a
                // verified inactive baseline in the committed namespace before startup applies intent.
                int port = runtime.Repositories.Session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>();
                await runtime.Network.RestoreConfigurationAsync(new NetworkSettingsConfiguration(
                    ClashSharpMode.Disabled, "builtin-direct", false, port), token).ConfigureAwait(false);
            }
        }, cancellationToken).ConfigureAwait(false);
        return pending;
    }

    public async Task CompleteAdmittedAsync(GenerationReplacementCheckpoint checkpoint, MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        admission.EnsureActiveExclusiveLease(lease);
        DataGenerationManifestSnapshot current = await RequireCurrentAsync(cancellationToken).ConfigureAwait(false);
        if (!current.Descriptor.IsSameGeneration(checkpoint.Baseline.Descriptor)
            && !(checkpoint.Candidate?.IsSameGeneration(current.Descriptor) ?? false))
        {
            throw new InvalidOperationException("The recovered directory no longer belongs to the saved replacement.");
        }
        await journal.CompleteAsync(checkpoint.OperationId, cancellationToken).ConfigureAwait(false);
    }

    private async Task<DataGenerationManifestSnapshot> RequireCurrentAsync(CancellationToken cancellationToken)
    {
        DataGenerationManifestSnapshot durable = await store.LoadCurrentAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The replacement recovery has no durable current directory.");
        if (durable.ContentHash != generations.CurrentManifest.ContentHash) { throw new InvalidOperationException("The loaded and durable recovery directories disagree."); }
        return durable;
    }

    private static bool IsCommittedDecision(GenerationReplacementCheckpoint pending, DataGenerationManifestSnapshot current)
    {
        if (current.ContentHash == pending.Baseline.ContentHash && current.Descriptor.IsSameGeneration(pending.Baseline.Descriptor)) { return false; }
        if (pending.Candidate is { } candidate && current.Descriptor.IsSameGeneration(candidate)
            && pending.Baseline.ManifestRevision < long.MaxValue && current.ManifestRevision == pending.Baseline.ManifestRevision + 1
            && current.HighestGenerationNumber == candidate.GenerationNumber) { return true; }
        throw new InvalidOperationException("The durable pointer does not match either recorded replacement decision.");
    }
}
