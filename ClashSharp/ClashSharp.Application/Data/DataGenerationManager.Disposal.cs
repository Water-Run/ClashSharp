namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationManager
{
    /// <summary>Disposes every owned scope after existing leases have left.</summary>
    public async ValueTask DisposeAsync()
    {
        TaskCompletionSource<object?> completion;
        Task ownershipDrainTask;
        bool ownsDisposal;
        TaskCompletionSource<DataGenerationTransition>? pendingDrain;
        lock (_syncLock)
        {
            bool retriesFailedCleanup = _state == ManagerState.DisposalFailed;
            ownsDisposal = _disposalCompletion is null || retriesFailedCleanup;
            if (ownsDisposal)
            {
                _disposalCompletion =
                    new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            completion = _disposalCompletion!;
            if (!ownsDisposal)
            {
                ownershipDrainTask = Task.CompletedTask;
                pendingDrain = null;
            }
            else
            {
                _state = ManagerState.Disposing;
                pendingDrain = _drainCompletion;
                _drainCompletion = null;
                _leaseDrainCompletion = retriesFailedCleanup || _leaseCount == 0
                    ? null
                    : new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
                Task leaseDrainTask =
                    _leaseDrainCompletion?.Task ?? Task.CompletedTask;
                Task transitionCompletionTask =
                    _transitionOperationCompletion?.Task ?? Task.CompletedTask;
                Task storeOperationTask =
                    _storeOperationCompletion?.Task ?? Task.CompletedTask;
                ownershipDrainTask = Task.WhenAll(
                    leaseDrainTask,
                    transitionCompletionTask,
                    storeOperationTask);
            }
        }

        pendingDrain?.TrySetException(new DataGenerationManagerException(
            DataGenerationManagerError.Disposed,
            "The generation manager is disposing."));
        if (ownsDisposal)
        {
            await CompleteDisposalAsync(ownershipDrainTask, completion).ConfigureAwait(false);
        }

        await completion.Task.ConfigureAwait(false);
    }

    private async Task CompleteDisposalAsync(
        Task leaseDrainTask,
        TaskCompletionSource<object?> completion)
    {
        try
        {
            await leaseDrainTask.ConfigureAwait(false);
            DataGenerationScope[] scopes;
            lock (_syncLock)
            {
                if (_shutdownScopes is null)
                {
                    DataGenerationTransition? transition = _transition;
                    _shutdownScopes =
                    [
                        .. new[]
                        {
                            _currentScope,
                            transition?.BaselineScope,
                            transition?.StagedScope,
                        }
                        .OfType<DataGenerationScope>()
                        .Distinct(),
                    ];
                    foreach (DataGenerationScope scope in _shutdownScopes)
                    {
                        scope.TransferOwnershipForDisposal(this, transition);
                    }

                    transition?.DetachOwner();
                }

                scopes = _shutdownScopes;
            }

            List<Exception> failures = [];
            foreach (DataGenerationScope scope in scopes)
            {
                try
                {
                    await scope.DisposeOwnedAsync(this).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            lock (_syncLock)
            {
                _currentScope = null;
                _currentManifest = null;
                _transition = null;
                _drainCompletion = null;
                _state = failures.Count == 0
                    ? ManagerState.Disposed
                    : ManagerState.DisposalFailed;
                if (failures.Count == 0)
                {
                    _shutdownScopes = null;
                }
            }

            if (failures.Count == 0)
            {
                completion.TrySetResult(null);
            }
            else
            {
                completion.TrySetException(new AggregateException(
                    "One or more generation scopes failed to dispose.",
                    failures));
            }
        }
        catch (Exception exception)
        {
            lock (_syncLock)
            {
                _state = ManagerState.DisposalFailed;
            }

            completion.TrySetException(exception);
        }
    }
}
