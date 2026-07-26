using System.Collections.ObjectModel;
using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>One presentation-safe trigger definition and its latest historical fire timestamp.</summary>
public sealed class TriggerDefinitionCatalogItem
{
    /// <summary>Initializes one validated immutable catalog item.</summary>
    public TriggerDefinitionCatalogItem(
        TriggerTaskDefinition definition,
        DateTimeOffset? lastTriggeredAt)
    {
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        if (!TriggerDefinitionValidator.Validate(definition).IsValid)
        {
            throw new ArgumentException("Catalog definitions must be valid.", nameof(definition));
        }

        LastTriggeredAt = lastTriggeredAt;
    }

    /// <summary>Gets the immutable definition.</summary>
    public TriggerTaskDefinition Definition { get; }

    /// <summary>Gets the latest historical fire timestamp, or null.</summary>
    public DateTimeOffset? LastTriggeredAt { get; }
}

/// <summary>Immutable definition-only projection used by presentation consumers.</summary>
public sealed class TriggerDefinitionCatalog
{
    /// <summary>Initializes one ordered catalog at an optimistic repository generation.</summary>
    public TriggerDefinitionCatalog(
        long generation,
        IEnumerable<TriggerDefinitionCatalogItem> tasks,
        IEnumerable<TriggerDiagnostic> diagnostics)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(generation);
        ArgumentNullException.ThrowIfNull(tasks);
        ArgumentNullException.ThrowIfNull(diagnostics);
        TriggerDefinitionCatalogItem[] taskArray = tasks.ToArray();
        TriggerDiagnostic[] diagnosticArray = diagnostics.ToArray();
        HashSet<string> ids = new(StringComparer.Ordinal);
        if (taskArray.Any(task => task is null || !ids.Add(task.Definition.Id)))
        {
            throw new ArgumentException(
                "Catalog tasks must be present and have unique identities.",
                nameof(tasks));
        }

        if (diagnosticArray.Any(static diagnostic => diagnostic is null))
        {
            throw new ArgumentException("Catalog diagnostics must be present.", nameof(diagnostics));
        }

        Generation = generation;
        Tasks = Array.AsReadOnly(taskArray);
        Diagnostics = Array.AsReadOnly(diagnosticArray);
    }

    /// <summary>Gets an uninitialized empty cache value.</summary>
    public static TriggerDefinitionCatalog Empty { get; } = new(0, [], []);

    /// <summary>Gets the optimistic definition generation.</summary>
    public long Generation { get; }

    /// <summary>Gets definitions in display and evaluation order.</summary>
    public ReadOnlyCollection<TriggerDefinitionCatalogItem> Tasks { get; }

    /// <summary>Gets current durable diagnostics.</summary>
    public ReadOnlyCollection<TriggerDiagnostic> Diagnostics { get; }
}

/// <summary>Host-singleton asynchronous facade for trigger definition presentation and CRUD.</summary>
public interface ITriggerDefinitionStore
{
    /// <summary>Gets the latest successfully observed or committed catalog without performing I/O.</summary>
    TriggerDefinitionCatalog Current { get; }

    /// <summary>Reads and caches the authoritative ordered definition catalog.</summary>
    Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(
        CancellationToken cancellationToken);

    /// <summary>Atomically replaces definitions and caches the committed generation.</summary>
    Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(
        long expectedGeneration,
        IReadOnlyList<TriggerTaskDefinition> definitions,
        CancellationToken cancellationToken);
}

/// <summary>Serializes presentation CRUD over the transactional repository and retains a safe summary cache.</summary>
public sealed class TriggerDefinitionStore : ITriggerDefinitionStore
{
    private readonly ITriggerRepository _repository;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private TriggerDefinitionCatalog _current = TriggerDefinitionCatalog.Empty;

    /// <summary>Initializes a facade over one host-owned repository.</summary>
    public TriggerDefinitionStore(ITriggerRepository repository, TimeProvider timeProvider)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <inheritdoc />
    public TriggerDefinitionCatalog Current => Volatile.Read(ref _current);

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(
        CancellationToken cancellationToken)
    {
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TriggerPersistenceResult<TriggerRepositorySnapshot> read =
                await _repository.ReadSnapshotAsync(cancellationToken).ConfigureAwait(false);
            if (!read.IsSucceeded || read.Value is not TriggerRepositorySnapshot snapshot)
            {
                return MapFailure<TriggerRepositorySnapshot, TriggerDefinitionCatalog>(read);
            }

            TriggerDefinitionCatalog catalog = Project(snapshot);
            Volatile.Write(ref _current, catalog);
            return TriggerPersistenceResult.Succeeded(catalog);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(
        long expectedGeneration,
        IReadOnlyList<TriggerTaskDefinition> definitions,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(expectedGeneration);
        ArgumentNullException.ThrowIfNull(definitions);
        TriggerDefinitionWriteRequest request = new(expectedGeneration, definitions);
        await _operationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            TriggerPersistenceResult written = await _repository
                .ReplaceDefinitionsAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!written.IsSucceeded)
            {
                return MapFailure<TriggerDefinitionCatalog>(written);
            }

            TriggerDefinitionCatalog previous = Current;
            Dictionary<string, DateTimeOffset?> timestamps = previous.Tasks.ToDictionary(
                static task => task.Definition.Id,
                static task => task.LastTriggeredAt,
                StringComparer.Ordinal);
            TriggerDefinitionCatalog committed = new(
                checked(expectedGeneration + 1),
                request.Definitions.Select(definition => new TriggerDefinitionCatalogItem(
                    definition,
                    timestamps.GetValueOrDefault(definition.Id))),
                previous.Diagnostics);
            Volatile.Write(ref _current, committed);
            return TriggerPersistenceResult.Succeeded(committed);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    private static TriggerDefinitionCatalog Project(TriggerRepositorySnapshot snapshot)
    {
        return new TriggerDefinitionCatalog(
            snapshot.DefinitionGeneration,
            snapshot.Tasks.Select(record => new TriggerDefinitionCatalogItem(
                record.Definition,
                record.State.LastTriggeredAt)),
            snapshot.Diagnostics);
    }

    private TriggerPersistenceResult<TTarget> MapFailure<TSource, TTarget>(
        TriggerPersistenceResult<TSource> result)
    {
        return result.Status switch
        {
            TriggerPersistenceStatus.Conflict => TriggerPersistenceResult.Conflict<TTarget>(),
            TriggerPersistenceStatus.NotFound => TriggerPersistenceResult.NotFound<TTarget>(),
            TriggerPersistenceStatus.Invalid => TriggerPersistenceResult.Invalid<TTarget>(
                result.Diagnostic ?? MissingDiagnostic("trigger.definition.read_invalid")),
            _ => TriggerPersistenceResult.Unavailable<TTarget>(
                result.Diagnostic ?? MissingDiagnostic("trigger.definition.read_unavailable")),
        };
    }

    private TriggerPersistenceResult<TTarget> MapFailure<TTarget>(
        TriggerPersistenceResult result)
    {
        return result.Status switch
        {
            TriggerPersistenceStatus.Conflict => TriggerPersistenceResult.Conflict<TTarget>(),
            TriggerPersistenceStatus.NotFound => TriggerPersistenceResult.NotFound<TTarget>(),
            TriggerPersistenceStatus.Invalid => TriggerPersistenceResult.Invalid<TTarget>(
                result.Diagnostic ?? MissingDiagnostic("trigger.definition.write_invalid")),
            _ => TriggerPersistenceResult.Unavailable<TTarget>(
                result.Diagnostic ?? MissingDiagnostic("trigger.definition.write_unavailable")),
        };
    }

    private TriggerDiagnostic MissingDiagnostic(string code)
    {
        return new TriggerDiagnostic(
            code,
            TriggerDiagnosticSeverity.Error,
            null,
            "definition-store:missing-diagnostic",
            _timeProvider.GetUtcNow());
    }
}
