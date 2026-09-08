namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationManager
{
    /// <summary>Resolves a generation-owned service and pins its complete asynchronous operation before transition can retire it.</summary>
    /// <typeparam name="TService">Service provided by the scope's owned lifetime through <see cref="IServiceProvider"/>.</typeparam>
    /// <typeparam name="TResult">Immutable operation result that can outlive the scope.</typeparam>
    /// <param name="operation">Owned operation; it must await all work and must not return a service or live repository handle.</param>
    /// <param name="cancellationToken">Cancels acquisition and is passed to the owned operation without abandoning its task.</param>
    public async Task<TResult> ExecuteAsync<TService, TResult>(
        Func<TService, DataGenerationDescriptor, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken) where TService : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using DataGenerationLease lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        TService service = lease.Scope.GetOwnedService<TService>();
        return await operation(service, lease.Descriptor, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Captures an immutable in-memory projection under a short synchronous generation pin without blocking on asynchronous work.</summary>
    /// <typeparam name="TService">Service provided by this generation's owned lifetime.</typeparam>
    /// <typeparam name="TResult">Immutable projection that can outlive the scope.</typeparam>
    /// <param name="capture">Pure synchronous reader; it must not perform I/O, start tasks, or return a live service or repository.</param>
    public TResult ReadSnapshot<TService, TResult>(Func<TService, DataGenerationDescriptor, TResult> capture)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(capture);
        using DataGenerationLease lease = AcquireCore(CancellationToken.None);
        return capture(lease.Scope.GetOwnedService<TService>(), lease.Descriptor);
    }
}
