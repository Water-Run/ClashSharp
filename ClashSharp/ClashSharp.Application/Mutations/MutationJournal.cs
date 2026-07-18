namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Records the durable state of one idempotent participant step.</summary>
/// <param name="Name">Stable participant step name.</param>
/// <param name="Phase">Latest durable phase for this step.</param>
/// <param name="IntentRecorded">Whether phase intent was flushed before the external side effect.</param>
/// <param name="Completed">Whether successful phase completion was flushed.</param>
/// <param name="CompensationData">Opaque versioned data required to restore the baseline.</param>
public sealed record MutationJournalStep(
    string Name,
    MutationJournalPhase Phase,
    bool IntentRecorded,
    bool Completed,
    string? CompensationData);

/// <summary>Contains the versioned, journal-ready state of one top-level mutation.</summary>
/// <param name="SchemaVersion">Journal document schema version.</param>
/// <param name="OperationId">Stable non-empty operation identifier.</param>
/// <param name="OperationType">Stable operation type.</param>
/// <param name="Generation">Monotonically increasing durable generation beginning at one.</param>
/// <param name="Phase">Latest durable operation phase.</param>
/// <param name="BaselineHash">Hash of the verified pre-mutation durable and external state.</param>
/// <param name="DesiredHash">Hash of the planned durable and external target state.</param>
/// <param name="HasCommitMarker">Whether the point-of-no-return marker is durable.</param>
/// <param name="Steps">Ordered participant steps and compensation material.</param>
public sealed record MutationJournal(
    int SchemaVersion,
    Guid OperationId,
    string OperationType,
    long Generation,
    MutationJournalPhase Phase,
    string BaselineHash,
    string DesiredHash,
    bool HasCommitMarker,
    IReadOnlyList<MutationJournalStep> Steps)
{
    /// <summary>Gets the only journal document schema supported by this build.</summary>
    public const int CurrentSchemaVersion = 1;
}

/// <summary>Pairs a validated journal document with the SHA-256 hash of its exact payload bytes.</summary>
/// <param name="Journal">Validated journal document.</param>
/// <param name="ContentHash">Lowercase hexadecimal SHA-256 payload hash.</param>
public sealed record MutationJournalSnapshot(MutationJournal Journal, string ContentHash);
