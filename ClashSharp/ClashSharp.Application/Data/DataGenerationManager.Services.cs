namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationManager
{
    /// <summary>Runs complete synchronous repository work under a generation pin, including synchronous file or SQLite access.</summary>
    /// <remarks>The operation must not start detached work or return a live repository, lazy sequence, or file handle.</remarks>
    public TResult Execute<TService, TResult>(Func<TService, DataGenerationDescriptor, TResult> operation)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        using DataGenerationLease lease = AcquireCore(CancellationToken.None);
        return operation(lease.Scope.GetOwnedService<TService>(), lease.Descriptor);
    }

    /// <summary>Runs a complete synchronous repository command before its generation can retire.</summary>
    public void Execute<TService>(Action<TService, DataGenerationDescriptor> operation)
        where TService : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        using DataGenerationLease lease = AcquireCore(CancellationToken.None);
        operation(lease.Scope.GetOwnedService<TService>(), lease.Descriptor);
    }

    /// <summary>Awaits a complete asynchronous repository command before releasing its generation.</summary>
    public async Task ExecuteAsync<TService>(
        Func<TService, DataGenerationDescriptor, CancellationToken, Task> operation,
        CancellationToken cancellationToken) where TService : class
    {
        ArgumentNullException.ThrowIfNull(operation);
        await using DataGenerationLease lease = await AcquireAsync(cancellationToken).ConfigureAwait(false);
        await operation(lease.Scope.GetOwnedService<TService>(), lease.Descriptor, cancellationToken).ConfigureAwait(false);
    }

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
