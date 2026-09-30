namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationTransition
{
    /// <summary>Runs complete candidate work under this transition without opening ordinary generation admission.</summary>
    /// <remarks>Await all nested work and return only detached results; never return a service or live handle.</remarks>
    /// <typeparam name="TService">Service owned by the staged candidate.</typeparam>
    /// <typeparam name="TResult">Detached result that can outlive the operation.</typeparam>
    /// <param name="operation">Complete candidate operation, serialized with publication and disposal.</param>
    /// <param name="cancellationToken">Cancels admission or the operation without abandoning its task.</param>
    public Task<TResult> ExecuteCandidateAsync<TService, TResult>(
        Func<TService, DataGenerationDescriptor, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) where TService : class =>
        ExecuteOwnedAsync(candidate: true, operation, cancellationToken);

    /// <summary>Runs complete baseline work for compensation while ordinary readers remain closed.</summary>
    /// <remarks>Await all nested work and return only detached results; never return a service or live handle.</remarks>
    /// <typeparam name="TService">Service owned by the drained baseline.</typeparam>
    /// <typeparam name="TResult">Detached result that can outlive the operation.</typeparam>
    /// <param name="operation">Complete baseline operation, serialized with publication and disposal.</param>
    /// <param name="cancellationToken">Cancels admission or the operation without abandoning its task.</param>
    public Task<TResult> ExecuteBaselineAsync<TService, TResult>(
        Func<TService, DataGenerationDescriptor, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) where TService : class =>
        ExecuteOwnedAsync(candidate: false, operation, cancellationToken);

    private async Task<TResult> ExecuteOwnedAsync<TService, TResult>(bool candidate,
        Func<TService, DataGenerationDescriptor, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) where TService : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        cancellationToken.ThrowIfCancellationRequested();
        DataGenerationManager owner = GetOwner();
        // The same exclusive slot covers manifest I/O and explicit native effects. Shutdown
        // waits for the accepted task, and no competing resolution can retire its dependencies.
        object ownership = owner.BeginStoreOperation(this);
        try
        {
            DataGenerationScope scope = candidate
                ? StagedScope ?? throw new InvalidOperationException("No candidate belongs to this transition.")
                : BaselineScope;
            return await operation(scope.GetOwnedService<TService>(), scope.Descriptor, cancellationToken).ConfigureAwait(false);
        }
        finally { owner.EndStoreOperation(this, ownership); }
    }
}
