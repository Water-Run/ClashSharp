using System.Collections.ObjectModel;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>One ordered trigger definition and its latest persistent latch state.</summary>
/// <param name="Order">Zero-based stable display/evaluation order.</param>
/// <param name="Definition">Immutable task definition.</param>
/// <param name="State">Latest persistent state.</param>
public sealed record TriggerTaskRecord(
    int Order,
    TriggerTaskDefinition Definition,
    TriggerTaskState State);

/// <summary>Immutable point-in-time repository snapshot.</summary>
public sealed class TriggerRepositorySnapshot
{
    /// <summary>Initializes one repository snapshot and defensively copies its collections.</summary>
    /// <param name="schemaVersion">Positive database schema version.</param>
    /// <param name="definitionGeneration">Nonnegative optimistic definition generation.</param>
    /// <param name="tasks">Tasks in contiguous zero-based order.</param>
    /// <param name="diagnostics">Current durable diagnostics.</param>
    public TriggerRepositorySnapshot(
        int schemaVersion,
        long definitionGeneration,
        IEnumerable<TriggerTaskRecord> tasks,
        IEnumerable<TriggerDiagnostic> diagnostics)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegative(definitionGeneration);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(diagnostics);

        TriggerTaskRecord[] taskArray = tasks.ToArray();
        TriggerDiagnostic[] diagnosticArray = diagnostics.ToArray();
        for (int index = 0; index < taskArray.Length; index++)
        {
            TriggerTaskRecord? task = taskArray[index];
            if (task is null
                || task.Order != index
                || task.Definition is null
                || task.State is null
                || !TriggerDefinitionValidator.Validate(task.Definition).IsValid
                || !StringComparer.Ordinal.Equals(task.Definition.Id, task.State.TaskId)
                || task.Definition.Revision != task.State.TaskRevision
                || task.Definition.Conditions.Count != task.State.ConditionStates.Count
                || task.Definition.Conditions.Any(
                    condition => !task.State.ConditionStates.ContainsKey(condition.Id)))
            {
                throw new ArgumentException(
                    "Tasks must be present, contiguous, and paired with matching state.",
                    nameof(tasks));
            }
        }

        if (diagnosticArray.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Diagnostics must be present.", nameof(diagnostics));
        }

        SchemaVersion = schemaVersion;
        DefinitionGeneration = definitionGeneration;
        Tasks = Array.AsReadOnly(taskArray);
        Diagnostics = Array.AsReadOnly(diagnosticArray);
    }

    /// <summary>Gets the positive database schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the optimistic definition generation.</summary>
    public long DefinitionGeneration { get; }

    /// <summary>Gets tasks in contiguous zero-based order.</summary>
    public ReadOnlyCollection<TriggerTaskRecord> Tasks { get; }

    /// <summary>Gets current durable diagnostics.</summary>
    public ReadOnlyCollection<TriggerDiagnostic> Diagnostics { get; }
}

/// <summary>Immutable request to replace the complete ordered trigger definition set.</summary>
public sealed class TriggerDefinitionWriteRequest
{
    /// <summary>Initializes a definition write request.</summary>
    /// <param name="expectedGeneration">Generation that must still be authoritative.</param>
    /// <param name="definitions">Complete ordered replacement definitions.</param>
    public TriggerDefinitionWriteRequest(
        long expectedGeneration,
        IEnumerable<TriggerTaskDefinition> definitions)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);
        ArgumentNullException.ThrowIfNull(definitions);
        TriggerTaskDefinition[] definitionArray = definitions.ToArray();
        HashSet<string> taskIds = new(StringComparer.Ordinal);
        if (definitionArray.Any(definition =>
                definition is null
                || !TriggerDefinitionValidator.Validate(definition).IsValid
                || !taskIds.Add(definition.Id)))
        {
            throw new ArgumentException(
                "Definitions must be valid and have unique task identities.",
                nameof(definitions));
        }

        ExpectedGeneration = expectedGeneration;
        Definitions = Array.AsReadOnly(definitionArray);
    }

    /// <summary>Gets the generation that must still be authoritative.</summary>
    public long ExpectedGeneration { get; }

    /// <summary>Gets the complete ordered replacement definitions.</summary>
    public ReadOnlyCollection<TriggerTaskDefinition> Definitions { get; }
}

/// <summary>Immutable request to import one verified legacy trigger snapshot atomically.</summary>
public sealed class TriggerMigrationImportRequest
{
    /// <summary>Initializes a validated legacy import request.</summary>
    /// <param name="expectedGeneration">Generation that must still be authoritative.</param>
    /// <param name="sourceHash">Lowercase SHA-256 hash of the captured legacy source.</param>
    /// <param name="tasks">Complete ordered migrated task and latch records.</param>
    /// <param name="diagnostics">Stable migration diagnostics to persist with the import.</param>
    public TriggerMigrationImportRequest(
        long expectedGeneration,
        string sourceHash,
        IEnumerable<TriggerTaskRecord> tasks,
        IEnumerable<TriggerDiagnostic> diagnostics)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(diagnostics);
        if (sourceHash.Length != 64
            || sourceHash.Any(character => !char.IsAsciiHexDigitLower(character)))
        {
            throw new ArgumentException(
                "Source hash must be a lowercase SHA-256 hexadecimal value.",
                nameof(sourceHash));
        }

        TriggerTaskRecord[] taskArray = tasks.ToArray();
        TriggerDiagnostic[] diagnosticArray = diagnostics.ToArray();
        _ = new TriggerRepositorySnapshot(1, expectedGeneration, taskArray, diagnosticArray);
        ExpectedGeneration = expectedGeneration;
        SourceHash = sourceHash;
        Tasks = Array.AsReadOnly(taskArray);
        Diagnostics = Array.AsReadOnly(diagnosticArray);
    }

    /// <summary>Gets the generation that must still be authoritative.</summary>
    public long ExpectedGeneration { get; }

    /// <summary>Gets the lowercase SHA-256 hash of the captured legacy source.</summary>
    public string SourceHash { get; }

    /// <summary>Gets complete ordered migrated task and latch records.</summary>
    public ReadOnlyCollection<TriggerTaskRecord> Tasks { get; }

    /// <summary>Gets stable migration diagnostics to persist with the import.</summary>
    public ReadOnlyCollection<TriggerDiagnostic> Diagnostics { get; }
}
