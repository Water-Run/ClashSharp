namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Identifies one top-level application mutation request.</summary>
/// <param name="OperationId">Stable non-empty identifier for this operation.</param>
/// <param name="OperationType">Stable non-empty operation type used by journals and diagnostics.</param>
/// <param name="RequiresQuiescence">Whether the operation requires exclusive admission and producer quiescence.</param>
public sealed record MutationRequest(Guid OperationId, string OperationType, bool RequiresQuiescence = false)
{
    /// <summary>Creates a validated mutation request.</summary>
    /// <param name="operationType">Stable non-empty operation type used by journals and diagnostics.</param>
    /// <param name="requiresQuiescence">Whether the operation requires exclusive admission and producer quiescence.</param>
    /// <returns>A request with a newly generated operation identifier.</returns>
    public static MutationRequest Create(string operationType, bool requiresQuiescence = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationType);
        return new MutationRequest(Guid.NewGuid(), operationType, requiresQuiescence);
    }
}
