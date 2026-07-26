using System.Runtime.ExceptionServices;

namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationTransition
{
    /// <summary>Promotes through the store and safely classifies any uncertain failure.</summary>
    /// <param name="store">Durable current-generation store.</param>
    /// <param name="cancellationToken">Cancels work only before the store commit boundary.</param>
    /// <returns>The verified promoted manifest.</returns>
    public async Task<DataGenerationManifestSnapshot> PromoteManifestAsync(
        IDataGenerationStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsResolved)
        {
            throw new DataGenerationManagerException(
                DataGenerationManagerError.InvalidTransition,
                "A resolved transition cannot promote another manifest.");
        }

        DataGenerationManager owner = GetOwner();
        DataGenerationScope stagedScope = StagedScope
            ?? throw new DataGenerationManagerException(
                DataGenerationManagerError.InvalidTransition,
                "A candidate scope must be staged before manifest promotion.");
        object operationToken = owner.BeginStoreOperation(this);
        try
        {
            DataGenerationManifestSnapshot promoted = await store
                .PromoteAsync(
                    stagedScope.Descriptor,
                    BaselineManifest.ContentHash,
                    cancellationToken)
                .ConfigureAwait(false);
            owner.AcknowledgeManifestPromotion(this, promoted, operationToken);
            return promoted;
        }
        catch (Exception failure)
        {
            DataGenerationManifestSnapshot? observed;
            try
            {
                observed = await store
                    .LoadCurrentAsync(CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception observationFailure)
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.ManifestPromotionUncertain,
                    "Manifest promotion failed and the durable pointer could not be classified.",
                    new AggregateException(failure, observationFailure));
            }

            if (IsExactBaseline(observed))
            {
                await owner
                    .AbortAsync(this, observed, operationToken)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _owner, null);
                ExceptionDispatchInfo.Capture(failure).Throw();
            }

            if (IsPromotedCandidate(observed, stagedScope))
            {
                owner.AcknowledgeManifestPromotion(this, observed!, operationToken);
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.ManifestPromotionCommitted,
                    "Manifest promotion completed durably but its original call failed.",
                    failure);
            }

            throw new DataGenerationManagerException(
                DataGenerationManagerError.ManifestPromotionUncertain,
                "Manifest promotion failed and the durable pointer is neither baseline nor candidate.",
                failure);
        }
        finally
        {
            owner.EndStoreOperation(this, operationToken);
        }
    }

    /// <summary>Restores the durable baseline and in-memory scope under one exclusive operation.</summary>
    /// <param name="store">Durable current-generation store.</param>
    /// <param name="cancellationToken">Cancels work only before the store commit boundary.</param>
    /// <returns>The verified restoration manifest.</returns>
    public async Task<DataGenerationManifestSnapshot> RestoreBaselineAsync(
        IDataGenerationStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        DataGenerationManager owner = GetOwner();
        if (IsRolledBack)
        {
            await owner
                .RollbackAsync(this, RestoredManifest!)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _owner, null);
            return RestoredManifest!;
        }

        cancellationToken.ThrowIfCancellationRequested();
        DataGenerationManifestSnapshot promoted = PromotedManifest
            ?? throw new DataGenerationManagerException(
                DataGenerationManagerError.InvalidTransition,
                "Durable promotion must be verified before baseline restoration.");
        object operationToken = owner.BeginStoreOperation(this);
        try
        {
            DataGenerationManifestSnapshot restored;
            try
            {
                restored = await store
                    .RestoreAsync(
                        BaselineManifest,
                        promoted.ContentHash,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                DataGenerationManifestSnapshot? observed;
                try
                {
                    observed = await store
                        .LoadCurrentAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                }
                catch (Exception observationFailure)
                {
                    throw new DataGenerationManagerException(
                        DataGenerationManagerError.ManifestRestorationUncertain,
                        "Manifest restoration failed and the durable pointer could not be classified.",
                        new AggregateException(failure, observationFailure));
                }

                if (IsExactSnapshot(observed, promoted))
                {
                    ExceptionDispatchInfo.Capture(failure).Throw();
                }

                if (IsRestoredBaseline(observed, promoted))
                {
                    await owner
                        .RollbackAsync(this, observed!, operationToken)
                        .ConfigureAwait(false);
                    Interlocked.Exchange(ref _owner, null);
                    throw new DataGenerationManagerException(
                        DataGenerationManagerError.ManifestRestorationCommitted,
                        "Baseline restoration completed durably but its original call failed.",
                        failure);
                }

                throw new DataGenerationManagerException(
                    DataGenerationManagerError.ManifestRestorationUncertain,
                    "Manifest restoration failed and the durable pointer is neither candidate nor baseline.",
                    failure);
            }

            await owner
                .RollbackAsync(this, restored, operationToken)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _owner, null);
            return restored;
        }
        finally
        {
            owner.EndStoreOperation(this, operationToken);
        }
    }

    /// <summary>Aborts a staged candidate only after the store proves the baseline is still current.</summary>
    /// <param name="store">Durable current-generation store.</param>
    /// <param name="cancellationToken">Cancels the verification read.</param>
    public async ValueTask AbortAsync(
        IDataGenerationStore store,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(store);
        DataGenerationManager owner = GetOwner();
        if (IsAborted)
        {
            await owner
                .AbortAsync(this, observedBaseline: null)
                .ConfigureAwait(false);
            Interlocked.Exchange(ref _owner, null);
            return;
        }

        object operationToken = owner.BeginStoreOperation(this);
        try
        {
            DataGenerationManifestSnapshot? observed = await store
                .LoadCurrentAsync(cancellationToken)
                .ConfigureAwait(false);
            if (IsExactBaseline(observed))
            {
                await owner
                    .AbortAsync(this, observed, operationToken)
                    .ConfigureAwait(false);
                Interlocked.Exchange(ref _owner, null);
                return;
            }

            DataGenerationScope? stagedScope = StagedScope;
            if (stagedScope is not null && IsPromotedCandidate(observed, stagedScope))
            {
                if (PromotedManifest is null)
                {
                    owner.AcknowledgeManifestPromotion(
                        this,
                        observed!,
                        operationToken);
                }

                throw new DataGenerationManagerException(
                    DataGenerationManagerError.ManifestPromotionCommitted,
                    "The staged candidate is already the durable current generation.");
            }

            throw new DataGenerationManagerException(
                DataGenerationManagerError.ManifestPromotionUncertain,
                "The durable pointer is neither the transition baseline nor its staged candidate.");
        }
        finally
        {
            owner.EndStoreOperation(this, operationToken);
        }
    }

    private bool IsExactBaseline(DataGenerationManifestSnapshot? observed)
    {
        return observed is not null
            && observed.Descriptor.IsSameGeneration(BaselineManifest.Descriptor)
            && observed.ManifestRevision == BaselineManifest.ManifestRevision
            && observed.HighestGenerationNumber == BaselineManifest.HighestGenerationNumber
            && string.Equals(
                observed.ContentHash,
                BaselineManifest.ContentHash,
                StringComparison.Ordinal);
    }

    private bool IsPromotedCandidate(
        DataGenerationManifestSnapshot? observed,
        DataGenerationScope stagedScope)
    {
        return observed is not null
            && BaselineManifest.ManifestRevision < long.MaxValue
            && observed.ManifestRevision == BaselineManifest.ManifestRevision + 1
            && observed.Descriptor.IsSameGeneration(stagedScope.Descriptor)
            && observed.HighestGenerationNumber == stagedScope.Descriptor.GenerationNumber
            && !string.Equals(
                observed.ContentHash,
                BaselineManifest.ContentHash,
                StringComparison.Ordinal);
    }

    private bool IsRestoredBaseline(
        DataGenerationManifestSnapshot? observed,
        DataGenerationManifestSnapshot promoted)
    {
        return observed is not null
            && promoted.ManifestRevision < long.MaxValue
            && observed.ManifestRevision == promoted.ManifestRevision + 1
            && observed.Descriptor.IsSameGeneration(BaselineManifest.Descriptor)
            && observed.HighestGenerationNumber == promoted.HighestGenerationNumber
            && !string.Equals(
                observed.ContentHash,
                promoted.ContentHash,
                StringComparison.Ordinal)
            && !string.Equals(
                observed.ContentHash,
                BaselineManifest.ContentHash,
                StringComparison.Ordinal);
    }

    private static bool IsExactSnapshot(
        DataGenerationManifestSnapshot? observed,
        DataGenerationManifestSnapshot expected)
    {
        return observed is not null
            && observed.Descriptor.IsSameGeneration(expected.Descriptor)
            && observed.ManifestRevision == expected.ManifestRevision
            && observed.HighestGenerationNumber == expected.HighestGenerationNumber
            && string.Equals(
                observed.ContentHash,
                expected.ContentHash,
                StringComparison.Ordinal);
    }
}
