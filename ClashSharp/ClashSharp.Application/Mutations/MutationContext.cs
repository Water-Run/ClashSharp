namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Proves that a participant is executing inside the owning top-level mutation gate.</summary>
public sealed class MutationContext
{
    private readonly object _ownershipToken;
    private MutationAdmissionLease? _admissionLease;
    private int _active = 1;

    internal MutationContext(Guid operationId, object ownershipToken)
    {
        OperationId = operationId;
        _ownershipToken = ownershipToken;
    }

    /// <summary>Gets the stable identifier of the owning top-level mutation.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the explicit admission authority for the active top-level mutation.</summary>
    public MutationAdmissionLease AdmissionLease => Volatile.Read(ref _admissionLease)
        ?? throw new InvalidOperationException(
            "The mutation context is not bound to active process admission.");

    internal void BindAdmissionLease(MutationAdmissionLease admissionLease)
    {
        ArgumentNullException.ThrowIfNull(admissionLease);
        if (Interlocked.CompareExchange(ref _admissionLease, admissionLease, null) is not null)
        {
            throw new InvalidOperationException("The mutation context already has admission authority.");
        }
    }

    internal void EnsureOwnedBy(object ownershipToken)
    {
        if (!ReferenceEquals(_ownershipToken, ownershipToken) || Volatile.Read(ref _active) == 0)
        {
            throw new InvalidOperationException("The mutation context is foreign or no longer active.");
        }
    }

    internal void Invalidate()
    {
        Interlocked.Exchange(ref _active, 0);
        Volatile.Write(ref _admissionLease, null);
    }
}
