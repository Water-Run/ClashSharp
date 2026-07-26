namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationManager
{
    internal object BeginStoreOperation(DataGenerationTransition transition)
    {
        lock (_syncLock)
        {
            ValidateTransitionUnderLock(transition);
            object operationToken = new();
            _storeOperationToken = operationToken;
            _storeOperationTransition = transition;
            _storeOperationCompletion =
                new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            return operationToken;
        }
    }

    internal void EndStoreOperation(
        DataGenerationTransition transition,
        object operationToken)
    {
        TaskCompletionSource<object?> completion;
        lock (_syncLock)
        {
            if (!ReferenceEquals(_storeOperationTransition, transition)
                || !ReferenceEquals(_storeOperationToken, operationToken)
                || _storeOperationCompletion is null)
            {
                throw new InvalidOperationException(
                    "The durable generation operation lost its ownership token.");
            }

            completion = _storeOperationCompletion;
            _storeOperationCompletion = null;
            _storeOperationTransition = null;
            _storeOperationToken = null;
        }

        completion.TrySetResult(null);
    }

    private void ValidateTransitionUnderLock(
        DataGenerationTransition transition,
        bool allowResolvedCleanup = false,
        object? storeOperationToken = null)
    {
        bool ownsStoreOperation = _storeOperationToken is not null
            && ReferenceEquals(_storeOperationToken, storeOperationToken)
            && ReferenceEquals(_storeOperationTransition, transition);
        if (_state is ManagerState.Disposed or ManagerState.DisposalFailed
            || _state == ManagerState.Disposing && !ownsStoreOperation)
        {
            throw new DataGenerationManagerException(
                DataGenerationManagerError.Disposed,
                "The generation manager is disposing.");
        }

        if (_state is not (ManagerState.Transitioning or ManagerState.Disposing)
            || !ReferenceEquals(_transition, transition)
            || _transitionCompletionInProgress
            || _storeOperationToken is not null && !ownsStoreOperation
            || transition.IsResolved && !allowResolvedCleanup)
        {
            throw new DataGenerationManagerException(
                DataGenerationManagerError.InvalidTransition,
                "The generation transition is stale or no longer owns admission.");
        }
    }
}
