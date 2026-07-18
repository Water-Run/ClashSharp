namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Proves that a participant is executing inside the owning top-level mutation gate.</summary>
public sealed class MutationContext
{
    private readonly object _ownershipToken;

    internal MutationContext(Guid operationId, object ownershipToken)
    {
        OperationId = operationId;
        _ownershipToken = ownershipToken;
    }

    /// <summary>Gets the stable identifier of the owning top-level mutation.</summary>
    public Guid OperationId { get; }

    internal void EnsureOwnedBy(object ownershipToken)
    {
        if (!ReferenceEquals(_ownershipToken, ownershipToken))
        {
            throw new InvalidOperationException("The mutation context does not belong to this coordinator.");
        }
    }
}
