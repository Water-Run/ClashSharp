namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationManager
{
    internal void Stage(
        DataGenerationTransition transition,
        DataGenerationScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        lock (_syncLock)
        {
            ValidateTransitionUnderLock(transition);
            DataGenerationManifestSnapshot baseline = transition.BaselineManifest;
            bool generationNumberIsNext =
                baseline.HighestGenerationNumber < long.MaxValue
                && scope.Descriptor.GenerationNumber == baseline.HighestGenerationNumber + 1;
            if (transition.StagedScope is not null
                || scope.State != DataGenerationScopeState.Staged
                || scope.Descriptor.GenerationId == baseline.Descriptor.GenerationId
                || PathsEqual(scope.Descriptor.RootPath, baseline.Descriptor.RootPath)
                || !generationNumberIsNext
                || !scope.TryClaimStaged(transition))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidStage,
                    "The candidate scope is duplicate, stale, skipped, or already active.");
            }

            transition.SetStagedScope(scope);
        }
    }

    internal void AcknowledgeManifestPromotion(
        DataGenerationTransition transition,
        DataGenerationManifestSnapshot promotedManifest,
        object? storeOperationToken = null)
    {
        ArgumentNullException.ThrowIfNull(promotedManifest);
        lock (_syncLock)
        {
            ValidateTransitionUnderLock(
                transition,
                storeOperationToken: storeOperationToken);
            DataGenerationScope? stagedScope = transition.StagedScope;
            DataGenerationManifestSnapshot baseline = transition.BaselineManifest;
            bool revisionIsNext =
                baseline.ManifestRevision < long.MaxValue
                && promotedManifest.ManifestRevision == baseline.ManifestRevision + 1;
            if (stagedScope is null
                || transition.PromotedManifest is not null
                || !promotedManifest.Descriptor.IsSameGeneration(stagedScope.Descriptor)
                || !revisionIsNext
                || promotedManifest.HighestGenerationNumber != stagedScope.Descriptor.GenerationNumber
                || string.Equals(
                    promotedManifest.ContentHash,
                    baseline.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "The promoted manifest does not match the staged transition.");
            }

            transition.SetPromotedManifest(promotedManifest);
        }
    }

    internal void SwapToPromoted(DataGenerationTransition transition)
    {
        lock (_syncLock)
        {
            ValidateTransitionUnderLock(transition);
            DataGenerationScope? stagedScope = transition.StagedScope;
            DataGenerationManifestSnapshot? promotedManifest = transition.PromotedManifest;
            if (stagedScope is null
                || promotedManifest is null
                || transition.IsSwapped)
            {
                throw new DataGenerationManagerException(
                    DataGenerationManagerError.InvalidTransition,
                    "A verified promoted scope is required before the in-memory swap.");
            }

            stagedScope.ActivateClaimed(transition);
            _currentScope = stagedScope;
            _currentManifest = promotedManifest;
            transition.MarkSwapped();
        }
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            left,
            right,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
    }
}
