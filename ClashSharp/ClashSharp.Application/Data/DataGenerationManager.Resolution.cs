namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationManager
{
    internal async ValueTask CommitAsync(DataGenerationTransition transition)
    {
        DataGenerationScope baselineScope;
        lock (_syncLock)
        {
            ValidateTransitionUnderLock(transition, allowResolvedCleanup: true);
            if (transition.IsResolved && !transition.IsCommitted)
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "Only a committed transition can retry retired baseline cleanup.");
            }

            if (!transition.IsCommitted
                && (!transition.IsSwapped || transition.PromotedManifest is null))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "A generation cannot commit before durable promotion and in-memory swap.");
            }

            if (!transition.IsCommitted)
            {
                baselineScope = transition.BaselineScope;
                baselineScope.Retire(this);
                transition.StagedScope!.TransferOwnership(transition, this);
                transition.MarkCommitted();
            }
            else
            {
                baselineScope = transition.BaselineScope;
            }

            BeginTransitionCompletionUnderLock();
        }

        await DisposeTransitionScopeAsync(baselineScope, this).ConfigureAwait(false);
        CompleteTransition(transition);
    }

    internal async ValueTask RollbackAsync(
        DataGenerationTransition transition,
        DataGenerationManifestSnapshot restoredManifest,
        object? storeOperationToken = null)
    {
        ArgumentNullException.ThrowIfNull(restoredManifest);
        DataGenerationScope? stagedScope;
        lock (_syncLock)
        {
            ValidateTransitionUnderLock(
                transition,
                allowResolvedCleanup: true,
                storeOperationToken: storeOperationToken);
            if (transition.IsResolved && !transition.IsRolledBack)
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "Only a rolled-back transition can retry candidate cleanup.");
            }

            stagedScope = transition.StagedScope;
            if (!transition.IsRolledBack)
            {
                DataGenerationManifestSnapshot? promoted = transition.PromotedManifest;
                DataGenerationManifestSnapshot baseline = transition.BaselineManifest;
                bool revisionIsNext = promoted is not null
                    && promoted.ManifestRevision < long.MaxValue
                    && restoredManifest.ManifestRevision == promoted.ManifestRevision + 1;
                if (promoted is null
                    || !restoredManifest.Descriptor.IsSameGeneration(baseline.Descriptor)
                    || !revisionIsNext
                    || restoredManifest.HighestGenerationNumber != promoted.HighestGenerationNumber
                    || string.Equals(
                        restoredManifest.ContentHash,
                        promoted.ContentHash,
                        StringComparison.Ordinal)
                    || string.Equals(
                        restoredManifest.ContentHash,
                        baseline.ContentHash,
                        StringComparison.Ordinal))
                {
                    throw new DataGenerationManagerException(
                        DataGenerationManagerError.InvalidTransition,
                        "The restoration manifest does not prove rollback to the exact baseline.");
                }

                stagedScope?.Retire(transition);
                transition.BaselineScope.RestoreActive(this);
                _currentScope = transition.BaselineScope;
                _currentManifest = restoredManifest;
                transition.MarkRolledBack(restoredManifest);
            }
            else if (!SnapshotsEqual(transition.RestoredManifest, restoredManifest))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "Rollback cleanup must use the manifest that crossed the restoration boundary.");
            }

            BeginTransitionCompletionUnderLock();
        }

        if (stagedScope is not null)
        {
            await DisposeTransitionScopeAsync(stagedScope, transition).ConfigureAwait(false);
        }

        CompleteTransition(transition);
    }

    internal async ValueTask AbortAsync(
        DataGenerationTransition transition,
        DataGenerationManifestSnapshot? observedBaseline,
        object? storeOperationToken = null)
    {
        DataGenerationScope? stagedScope;
        lock (_syncLock)
        {
            ValidateTransitionUnderLock(
                transition,
                allowResolvedCleanup: true,
                storeOperationToken: storeOperationToken);
            if (transition.IsResolved && !transition.IsAborted)
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "Only an aborted transition can retry candidate cleanup.");
            }

            DataGenerationScope? candidate = transition.StagedScope;
            bool exactBaseline = observedBaseline is not null
                && observedBaseline.Descriptor.IsSameGeneration(
                    transition.BaselineManifest.Descriptor)
                && observedBaseline.ManifestRevision
                    == transition.BaselineManifest.ManifestRevision
                && observedBaseline.HighestGenerationNumber
                    == transition.BaselineManifest.HighestGenerationNumber
                && string.Equals(
                    observedBaseline.ContentHash,
                    transition.BaselineManifest.ContentHash,
                    StringComparison.Ordinal);
            if (!transition.IsAborted
                && (transition.PromotedManifest is not null
                    || transition.IsSwapped
                    || candidate is not null && !exactBaseline))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "A promoted generation requires verified rollback or explicit commit.");
            }

            stagedScope = candidate;
            if (!transition.IsAborted)
            {
                stagedScope?.Retire(transition);
                transition.BaselineScope.RestoreActive(this);
                _currentScope = transition.BaselineScope;
                _currentManifest = transition.BaselineManifest;
                transition.MarkAborted();
            }

            BeginTransitionCompletionUnderLock();
        }

        if (stagedScope is not null)
        {
            await DisposeTransitionScopeAsync(stagedScope, transition).ConfigureAwait(false);
        }

        CompleteTransition(transition);
    }

    private void BeginTransitionCompletionUnderLock()
    {
        _transitionCompletionInProgress = true;
        _transitionOperationCompletion =
            new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private async ValueTask DisposeTransitionScopeAsync(
        DataGenerationScope scope,
        object ownerToken)
    {
        try
        {
            await scope.DisposeOwnedAsync(ownerToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            TaskCompletionSource<object?>? operationCompletion;
            lock (_syncLock)
            {
                _transitionCompletionInProgress = false;
                operationCompletion = _transitionOperationCompletion;
                _transitionOperationCompletion = null;
            }

            operationCompletion?.TrySetResult(null);
            throw new DataGenerationManagerException(
                DataGenerationManagerError.ScopeDisposalFailed,
                "A retired generation scope could not be disposed safely.",
                exception);
        }
    }

    private void CompleteTransition(DataGenerationTransition transition)
    {
        TaskCompletionSource<object?>? operationCompletion;
        lock (_syncLock)
        {
            if (!ReferenceEquals(_transition, transition)
                || !_transitionCompletionInProgress
                || _currentScope is null
                || _currentManifest is null
                || _state is not (ManagerState.Transitioning or ManagerState.Disposing))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "The generation transition lost ownership before completion.");
            }

            _transitionCompletionInProgress = false;
            operationCompletion = _transitionOperationCompletion;
            _transitionOperationCompletion = null;
            _transition = null;
            if (_state == ManagerState.Transitioning)
            {
                _state = ManagerState.Active;
            }
        }

        operationCompletion?.TrySetResult(null);
    }

    private static bool SnapshotsEqual(
        DataGenerationManifestSnapshot? left,
        DataGenerationManifestSnapshot right)
    {
        return left is not null
            && left.Descriptor.IsSameGeneration(right.Descriptor)
            && left.ManifestRevision == right.ManifestRevision
            && left.HighestGenerationNumber == right.HighestGenerationNumber
            && string.Equals(left.ContentHash, right.ContentHash, StringComparison.Ordinal);
    }
}
