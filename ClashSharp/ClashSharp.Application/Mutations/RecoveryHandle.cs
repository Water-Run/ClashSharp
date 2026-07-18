namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Restricts one recovery attempt to the latest journal generation and direction.</summary>
public sealed class RecoveryHandle
{
    internal RecoveryHandle(Guid operationId, long generation, string currentHash, RecoveryDirection direction)
    {
        OperationId = operationId;
        Generation = generation;
        CurrentHash = currentHash;
        Direction = direction;
    }

    /// <summary>Gets the only operation this handle may recover.</summary>
    public Guid OperationId { get; }

    /// <summary>Gets the latest durable generation authorized for this attempt.</summary>
    public long Generation { get; private set; }

    /// <summary>Gets the latest durable content hash authorized for this attempt.</summary>
    public string CurrentHash { get; private set; }

    /// <summary>Gets whether the attempt may restore the baseline or only complete forward.</summary>
    public RecoveryDirection Direction { get; }

    internal void Advance(MutationJournalSnapshot snapshot)
    {
        if (snapshot.Journal.OperationId != OperationId || snapshot.Journal.Generation != Generation + 1)
        {
            throw new InvalidOperationException("A recovery handle can advance only to the next generation of the same operation.");
        }

        Generation = snapshot.Journal.Generation;
        CurrentHash = snapshot.ContentHash;
    }
}

/// <summary>Identifies the only state transition permitted by a recovery handle.</summary>
public enum RecoveryDirection
{
    /// <summary>Compensate an uncommitted operation back to its baseline.</summary>
    RestoreBaseline,

    /// <summary>Complete activation, verification, and cleanup of a committed target.</summary>
    CompleteForward,
}
