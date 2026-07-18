namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Owns all top-level journaled mutations and retained recovery attempts.</summary>
public interface IApplicationMutationCoordinator
{
    /// <summary>Executes one planned mutation under admission, the fair gate, and durable journaling.</summary>
    /// <typeparam name="T">Type of the verified result returned after cleanup.</typeparam>
    /// <param name="request">Stable top-level mutation request.</param>
    /// <param name="plan">Validated, journal-ready mutation plan.</param>
    /// <param name="resultFactory">Reads the verified result before gate release.</param>
    /// <param name="cancellationToken">Cancels admission and pre-side-effect work.</param>
    /// <returns>A typed durable mutation outcome.</returns>
    Task<MutationResult<T>> ExecuteAsync<T>(
        MutationRequest request,
        MutationPlan plan,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        CancellationToken cancellationToken);

    /// <summary>Plans and executes one mutation while already holding the fair mutation gate.</summary>
    /// <typeparam name="T">Type of the verified result returned after cleanup.</typeparam>
    /// <param name="request">Stable top-level mutation request.</param>
    /// <param name="planFactory">Read-only plan factory invoked under a valid mutation context before journaling.</param>
    /// <param name="resultFactory">Reads the verified result before journal deletion and gate release.</param>
    /// <param name="cancellationToken">Cancels admission and pre-side-effect work.</param>
    /// <returns>A typed durable mutation outcome.</returns>
    Task<MutationResult<T>> ExecuteAsync<T>(
        MutationRequest request,
        Func<MutationContext, CancellationToken, Task<MutationPlan>> planFactory,
        Func<MutationContext, CancellationToken, Task<T>> resultFactory,
        CancellationToken cancellationToken);

    /// <summary>Rejects contexts that were issued by another coordinator or have left their gate callback.</summary>
    /// <param name="context">Context to validate.</param>
    void EnsureContextOwnership(MutationContext context);

    /// <summary>Retries only the retained recovery operation with the supplied identifier.</summary>
    /// <param name="operationId">Identifier of the retained operation.</param>
    /// <param name="cancellationToken">Cancels waiting and read-only recovery preparation.</param>
    /// <returns>A typed recovery outcome.</returns>
    Task<MutationResult<object?>> RetryRecoveryAsync(Guid operationId, CancellationToken cancellationToken);
}
