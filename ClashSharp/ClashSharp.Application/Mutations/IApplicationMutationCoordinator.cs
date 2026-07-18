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

    /// <summary>Retries only the retained recovery operation with the supplied identifier.</summary>
    /// <param name="operationId">Identifier of the retained operation.</param>
    /// <param name="cancellationToken">Cancels waiting and read-only recovery preparation.</param>
    /// <returns>A typed recovery outcome.</returns>
    Task<MutationResult<object?>> RetryRecoveryAsync(Guid operationId, CancellationToken cancellationToken);
}
