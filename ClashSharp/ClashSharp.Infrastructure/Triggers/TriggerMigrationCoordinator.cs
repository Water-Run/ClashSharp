using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Infrastructure.Triggers;

/// <summary>Identifies the durable outcome of one legacy trigger migration attempt.</summary>
public enum TriggerMigrationStatus
{
    /// <summary>No legacy source existed; an empty repository is ready.</summary>
    NoSource = 0,

    /// <summary>A valid database was already authoritative and the legacy source was untouched.</summary>
    ExistingDatabasePreferred = 1,

    /// <summary>The captured legacy source was imported and retained as a migration backup.</summary>
    Migrated = 2,

    /// <summary>An invalid whole document was diagnosed and quarantined.</summary>
    Quarantined = 3,

    /// <summary>A prior committed migration's retained source backup was finalized.</summary>
    Finalized = 4,

    /// <summary>Storage was unavailable and no sound migration decision was possible.</summary>
    Unavailable = 5,
}

/// <summary>Typed result of one legacy trigger migration attempt.</summary>
public sealed class TriggerMigrationResult
{
    internal TriggerMigrationResult(
        TriggerMigrationStatus status,
        IEnumerable<TriggerDiagnostic> diagnostics)
    {
        Status = status;
        Diagnostics = Array.AsReadOnly(diagnostics.ToArray());
    }

    /// <summary>Gets the durable migration outcome.</summary>
    public TriggerMigrationStatus Status { get; }

    /// <summary>Gets diagnostics observed during this attempt.</summary>
    public ReadOnlyCollection<TriggerDiagnostic> Diagnostics { get; }
}

/// <summary>Coordinates one-time legacy JSON capture, validation, import, and quarantine.</summary>
public sealed class TriggerMigrationCoordinator
{
    private static readonly string[] TimeFormats =
    [
        "HH:mm",
        "HH:mm:ss",
        "HH:mm:ss.FFFFFFF",
    ];

    private readonly SqliteTriggerRepository _repository;
    private readonly string _legacyPath;
    private readonly TriggerMigrationIntentStore _intentStore;
    private readonly TimeProvider _timeProvider;

    /// <summary>Initializes a coordinator for one repository and legacy source path.</summary>
    /// <param name="repository">Concrete transactional trigger repository.</param>
    /// <param name="legacyPath">Path to migration-only <c>Triggers.json</c>.</param>
    /// <param name="timeProvider">Optional timestamp provider for artifact names and diagnostics.</param>
    public TriggerMigrationCoordinator(
        SqliteTriggerRepository repository,
        string legacyPath,
        TimeProvider? timeProvider = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyPath);
        _legacyPath = Path.GetFullPath(legacyPath);
        _intentStore = new TriggerMigrationIntentStore(_legacyPath);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Imports a legacy source only when no valid database authority already exists.</summary>
    public async Task<TriggerMigrationResult> MigrateAsync(CancellationToken cancellationToken)
    {
        bool databaseExisted = File.Exists(_repository.DatabasePath);
        string? expectedSourceHash = null;
        TriggerRepositorySnapshot? snapshot = null;
        if (databaseExisted)
        {
            TriggerPersistenceResult<TriggerRepositorySnapshot> opened =
                await _repository.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!opened.IsSucceeded || opened.Value is null)
            {
                return Unavailable(opened.Diagnostic);
            }

            snapshot = opened.Value;
            string? sourceHash;
            try
            {
                sourceHash = await _repository.ReadLegacyMigrationSourceHashAsync(
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException
                or UnauthorizedAccessException
                or InvalidDataException
                or FormatException
                or Microsoft.Data.Sqlite.SqliteException)
            {
                return Unavailable(CreateDiagnostic(
                    "trigger.migration.marker_unavailable",
                    null,
                    "marker:" + exception.GetType().Name));
            }
            if (sourceHash is not null)
            {
                return await FinalizeCommittedMigrationAsync(
                    sourceHash,
                    snapshot,
                    cancellationToken).ConfigureAwait(false);
            }

            bool safeEmpty = snapshot.Diagnostics.Any(
                diagnostic => diagnostic.Code == "trigger.storage.safe_empty");
            if (!safeEmpty
                && (snapshot.DefinitionGeneration != 0
                    || snapshot.Tasks.Count != 0
                    || snapshot.Diagnostics.Count != 0))
            {
                return new TriggerMigrationResult(
                    TriggerMigrationStatus.ExistingDatabasePreferred,
                    []);
            }

            if (!safeEmpty)
            {
                try
                {
                    expectedSourceHash = await _intentStore.ReadAsync(
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException
                    or UnauthorizedAccessException
                    or InvalidDataException)
                {
                    return Unavailable(CreateDiagnostic(
                        "trigger.migration.intent_unavailable",
                        null,
                        "intent:" + exception.GetType().Name));
                }

                if (expectedSourceHash is null)
                {
                    return new TriggerMigrationResult(
                        TriggerMigrationStatus.ExistingDatabasePreferred,
                        []);
                }
            }
        }

        if (!File.Exists(_legacyPath))
        {
            if (snapshot is null)
            {
                TriggerPersistenceResult<TriggerRepositorySnapshot> opened =
                    await _repository.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (!opened.IsSucceeded)
                {
                    return Unavailable(opened.Diagnostic);
                }
            }

            try
            {
                _intentStore.Delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Unavailable(CreateDiagnostic(
                    "trigger.migration.intent_cleanup_failed",
                    null,
                    "intent:" + exception.GetType().Name));
            }

            return new TriggerMigrationResult(TriggerMigrationStatus.NoSource, []);
        }

        LegacyTriggerDocument document;
        try
        {
            document = await LegacyTriggerMigrationReader.ReadAsync(
                _legacyPath,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Unavailable(CreateDiagnostic(
                "trigger.migration.source_unavailable",
                null,
                "source:" + exception.GetType().Name));
        }

        if (expectedSourceHash is not null
            && !StringComparer.Ordinal.Equals(expectedSourceHash, document.SourceHash))
        {
            return new TriggerMigrationResult(
                TriggerMigrationStatus.ExistingDatabasePreferred,
                [CreateDiagnostic(
                    "trigger.migration.source_conflict",
                    null,
                    "intent:hash_mismatch")]);
        }

        try
        {
            await _intentStore.EnsureAsync(
                document.SourceHash,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or InvalidDataException)
        {
            return Unavailable(CreateDiagnostic(
                "trigger.migration.intent_unavailable",
                null,
                "intent:" + exception.GetType().Name));
        }

        if (snapshot is null)
        {
            TriggerPersistenceResult<TriggerRepositorySnapshot> opened =
                await _repository.OpenAsync(cancellationToken).ConfigureAwait(false);
            if (!opened.IsSucceeded || opened.Value is null)
            {
                return Unavailable(opened.Diagnostic);
            }

            snapshot = opened.Value;
        }

        return await ImportCapturedDocumentAsync(
            document,
            snapshot,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<TriggerMigrationResult> ImportCapturedDocumentAsync(
        LegacyTriggerDocument document,
        TriggerRepositorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        List<TriggerDiagnostic> diagnostics = [];
        List<LegacyTaskQuarantine> quarantinedTasks = [];
        List<ParsedLegacyTask> parsedTasks = [];
        if (!document.IsValidShape)
        {
            diagnostics.Add(CreateDiagnostic(
                "trigger.migration.document.quarantined",
                null,
                document.DocumentErrorCode!));
        }
        else
        {
            LegacySchema schema = DetermineSchema(document.Tasks);
            for (int index = 0; index < document.Tasks.Count; index++)
            {
                JsonElement task = document.Tasks[index];
                try
                {
                    LegacySchema taskSchema = schema == LegacySchema.Mixed
                        ? DetermineSchema([task])
                        : schema;
                    parsedTasks.Add(ParseTask(task, index, taskSchema));
                }
                catch (LegacyTaskException exception)
                {
                    string? taskId = TryReadOptionalString(task, "Id");
                    diagnostics.Add(CreateDiagnostic(
                        "trigger.migration.task.quarantined",
                        taskId,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"task[{index}]:{exception.ErrorCode}")));
                    quarantinedTasks.Add(new LegacyTaskQuarantine(
                        index,
                        exception.ErrorCode,
                        task.GetRawText()));
                }
            }
        }

        List<TriggerTaskRecord> records = NormalizeAndBuildRecords(
            parsedTasks,
            document.SourceHash,
            diagnostics);
        if (quarantinedTasks.Count > 0)
        {
            try
            {
                await WriteTaskQuarantineAsync(
                    document.SourceHash,
                    quarantinedTasks,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Unavailable(CreateDiagnostic(
                    "trigger.migration.task_quarantine_failed",
                    null,
                    "quarantine:" + exception.GetType().Name), diagnostics);
            }
        }

        TriggerPersistenceResult imported = await _repository.TryImportMigrationAsync(
            new TriggerMigrationImportRequest(
                snapshot.DefinitionGeneration,
                document.SourceHash,
                records,
                diagnostics),
            cancellationToken).ConfigureAwait(false);
        if (!imported.IsSucceeded)
        {
            if (imported.Status != TriggerPersistenceStatus.Conflict)
            {
                return Unavailable(imported.Diagnostic, diagnostics);
            }

            try
            {
                _intentStore.Delete();
                return new TriggerMigrationResult(
                    TriggerMigrationStatus.ExistingDatabasePreferred,
                    diagnostics);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Unavailable(CreateDiagnostic(
                    "trigger.migration.intent_cleanup_failed",
                    null,
                    "intent:" + exception.GetType().Name), diagnostics);
            }
        }

        try
        {
            await MoveCapturedSourceAsync(
                document.SourceHash,
                quarantineWholeDocument: !document.IsValidShape,
                cancellationToken).ConfigureAwait(false);
            _intentStore.Delete();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(CreateDiagnostic(
                "trigger.migration.source_finalize_failed",
                null,
                "source:" + exception.GetType().Name));
            return new TriggerMigrationResult(TriggerMigrationStatus.Unavailable, diagnostics);
        }

        return new TriggerMigrationResult(
            document.IsValidShape
                ? TriggerMigrationStatus.Migrated
                : TriggerMigrationStatus.Quarantined,
            diagnostics);
    }

    private async Task<TriggerMigrationResult> FinalizeCommittedMigrationAsync(
        string sourceHash,
        TriggerRepositorySnapshot snapshot,
        CancellationToken cancellationToken)
    {
        if (File.Exists(_legacyPath))
        {
            try
            {
                string observedHash = await LegacyTriggerMigrationReader.ComputeHashAsync(
                    _legacyPath,
                    cancellationToken).ConfigureAwait(false);
                if (!StringComparer.Ordinal.Equals(observedHash, sourceHash))
                {
                    return new TriggerMigrationResult(
                        TriggerMigrationStatus.ExistingDatabasePreferred,
                        [CreateDiagnostic(
                            "trigger.migration.source_conflict",
                            null,
                            "source:hash_mismatch")]);
                }

                bool quarantine = snapshot.Diagnostics.Any(
                    diagnostic => diagnostic.Code == "trigger.migration.document.quarantined");
                await MoveCapturedSourceAsync(
                    sourceHash,
                    quarantine,
                    cancellationToken).ConfigureAwait(false);
                _intentStore.Delete();
                return new TriggerMigrationResult(TriggerMigrationStatus.Finalized, []);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                return Unavailable(CreateDiagnostic(
                    "trigger.migration.source_finalize_failed",
                    null,
                    "source:" + exception.GetType().Name));
            }
        }

        try
        {
            string prefix = Path.GetFileName(_legacyPath)
                + ".migration-backup."
                + sourceHash[..12]
                + ".";
            string directory = Path.GetDirectoryName(_legacyPath)!;
            string[] retainedBackups = Directory.GetFiles(directory, prefix + "*");
            if (retainedBackups.Length == 0)
            {
                _intentStore.Delete();
                return new TriggerMigrationResult(
                    TriggerMigrationStatus.ExistingDatabasePreferred,
                    []);
            }

            foreach (string retainedBackup in retainedBackups)
            {
                File.Delete(retainedBackup);
            }

            _intentStore.Delete();

            return new TriggerMigrationResult(TriggerMigrationStatus.Finalized, []);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unavailable(CreateDiagnostic(
                "trigger.migration.backup_cleanup_failed",
                null,
                "backup:" + exception.GetType().Name));
        }
    }

    private List<TriggerTaskRecord> NormalizeAndBuildRecords(
        IReadOnlyList<ParsedLegacyTask> parsedTasks,
        string sourceHash,
        ICollection<TriggerDiagnostic> diagnostics)
    {
        HashSet<string> taskIds = new(StringComparer.Ordinal);
        HashSet<string> taskNames = new(StringComparer.Ordinal);
        List<TriggerTaskRecord> records = new(parsedTasks.Count);
        for (int index = 0; index < parsedTasks.Count; index++)
        {
            ParsedLegacyTask parsed = parsedTasks[index];
            int sourceIndex = parsed.SourceIndex;
            string taskId = parsed.Definition.Id;
            if (!taskIds.Add(taskId))
            {
                taskId = string.Create(
                    CultureInfo.InvariantCulture,
                    $"legacy-{sourceHash[..16]}-{sourceIndex:D4}");
                while (!taskIds.Add(taskId))
                {
                    taskId += "x";
                }

                diagnostics.Add(CreateDiagnostic(
                    "trigger.migration.task_id.normalized",
                    taskId,
                    string.Create(CultureInfo.InvariantCulture, $"task[{sourceIndex}]:duplicate_id")));
            }

            string taskName = parsed.Definition.Name;
            if (!taskNames.Add(taskName))
            {
                string baseName = taskName;
                int suffix = 2;
                do
                {
                    taskName = string.Create(
                        CultureInfo.InvariantCulture,
                        $"{baseName} ({suffix++})");
                }
                while (!taskNames.Add(taskName));

                diagnostics.Add(CreateDiagnostic(
                    "trigger.migration.task_name.normalized",
                    taskId,
                    string.Create(CultureInfo.InvariantCulture, $"task[{sourceIndex}]:duplicate_name")));
            }

            TriggerTaskDefinition definition = new(
                taskId,
                parsed.Definition.Revision,
                taskName,
                parsed.Definition.IsEnabled,
                parsed.Definition.Conditions,
                parsed.Definition.Actions);
            TriggerTaskState state = CreateMigratedState(definition, parsed.LastTriggeredAt);
            records.Add(new TriggerTaskRecord(index, definition, state));
        }

        return records;
    }

    private static ParsedLegacyTask ParseTask(
        JsonElement element,
        int taskIndex,
        LegacySchema schema)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new LegacyTaskException("task_not_object");
        }

        string id = ReadRequiredString(element, "Id", "id_invalid");
        string name = ReadRequiredString(element, "Name", "name_invalid");
        bool isEnabled = ReadRequiredBoolean(element, "IsEnabled", "enabled_invalid");
        JsonElement conditionsElement = ReadRequiredArray(
            element,
            "Conditions",
            "conditions_invalid");
        JsonElement actionsElement = ReadRequiredArray(element, "Actions", "actions_invalid");
        List<TriggerCondition> conditions = [];
        int conditionIndex = 0;
        foreach (JsonElement condition in conditionsElement.EnumerateArray())
        {
            conditions.Add(ParseCondition(condition, conditionIndex++, schema));
        }

        List<TriggerAction> actions = [];
        foreach (JsonElement action in actionsElement.EnumerateArray())
        {
            actions.Add(ParseAction(action, schema));
        }

        DateTimeOffset? lastTriggeredAt = ReadLastTriggeredAt(element);
        TriggerTaskDefinition definition = new(
            id,
            1,
            name,
            isEnabled,
            conditions,
            actions);
        TriggerDefinitionValidationResult validation = TriggerDefinitionValidator.Validate(definition);
        if (!validation.IsValid)
        {
            throw new LegacyTaskException(validation.Errors[0].Code);
        }

        return new ParsedLegacyTask(taskIndex, definition, lastTriggeredAt);
    }

    private static TriggerCondition ParseCondition(
        JsonElement element,
        int index,
        LegacySchema schema)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new LegacyTaskException("condition_not_object");
        }

        int kind = ReadRequiredInt32(element, "Kind", "condition_kind_invalid");
        long threshold = ReadOptionalInt64(element, "Threshold");
        string value = ReadOptionalString(element, "Value");
        string id = string.Create(CultureInfo.InvariantCulture, $"legacy-condition-{index:D4}");
        return schema switch
        {
            LegacySchema.Initial => ParseInitialCondition(id, kind, threshold, value),
            LegacySchema.Current => ParseCurrentCondition(id, kind, threshold, value),
            LegacySchema.Shared when kind is >= 0 and <= 4 =>
                ParseCurrentCondition(id, kind, threshold, value),
            _ => throw new LegacyTaskException("condition_schema_ambiguous"),
        };
    }

    private static TriggerCondition ParseInitialCondition(
        string id,
        int kind,
        long threshold,
        string value)
    {
        return kind switch
        {
            0 => EventCondition(id, TriggerEventKind.AppEntered),
            1 => EventCondition(id, TriggerEventKind.ProxyStarted),
            2 => NotificationCondition(id, value),
            3 => TrafficCondition(id, TriggerTrafficScope.AllTime, threshold),
            4 => TrafficCondition(id, TriggerTrafficScope.RollingWindow, threshold),
            5 => RuntimeCondition(id, threshold),
            6 => SystemTimeCondition(id, value),
            _ => throw new LegacyTaskException("condition_kind_undefined"),
        };
    }

    private static TriggerCondition ParseCurrentCondition(
        string id,
        int kind,
        long threshold,
        string value)
    {
        return kind switch
        {
            0 => EventCondition(id, TriggerEventKind.AppEntered),
            1 => EventCondition(id, TriggerEventKind.ProxyStarted),
            2 => NotificationCondition(id, value),
            3 => TrafficCondition(id, TriggerTrafficScope.AllTime, threshold),
            4 => TrafficCondition(id, TriggerTrafficScope.RollingWindow, threshold),
            5 => RateCondition(id, TriggerTrafficDirection.Upload, threshold),
            6 => RateCondition(id, TriggerTrafficDirection.Download, threshold),
            7 when threshold is > 0 and <= int.MaxValue => new TriggerCondition(
                id,
                TriggerConditionKind.ActiveConnections,
                new ActiveConnectionsConditionParameters((int)threshold)),
            7 => throw new LegacyTaskException("condition_threshold_invalid"),
            8 => TrafficCondition(id, TriggerTrafficScope.CurrentSession, threshold),
            9 => RuntimeCondition(id, threshold),
            10 => SystemTimeCondition(id, value),
            _ => throw new LegacyTaskException("condition_kind_undefined"),
        };
    }

    private static TriggerCondition EventCondition(string id, TriggerEventKind eventKind)
    {
        return new TriggerCondition(
            id,
            TriggerConditionKind.Event,
            new EventConditionParameters(eventKind));
    }

    private static TriggerCondition NotificationCondition(string id, string value)
    {
        if (!Enum.TryParse(value, ignoreCase: false, out TriggerNotificationLevel level)
            || !Enum.IsDefined(level))
        {
            throw new LegacyTaskException("notification_level_invalid");
        }

        return new TriggerCondition(
            id,
            TriggerConditionKind.Notification,
            new NotificationConditionParameters(level));
    }

    private static TriggerCondition TrafficCondition(
        string id,
        TriggerTrafficScope scope,
        long threshold)
    {
        if (threshold <= 0)
        {
            throw new LegacyTaskException("condition_threshold_invalid");
        }

        return new TriggerCondition(
            id,
            TriggerConditionKind.Traffic,
            new TrafficConditionParameters(
                scope,
                threshold,
                scope == TriggerTrafficScope.RollingWindow
                    ? TimeSpan.FromMinutes(5)
                    : null));
    }

    private static TriggerCondition RateCondition(
        string id,
        TriggerTrafficDirection direction,
        long threshold)
    {
        if (threshold <= 0)
        {
            throw new LegacyTaskException("condition_threshold_invalid");
        }

        return new TriggerCondition(
            id,
            TriggerConditionKind.Rate,
            new RateConditionParameters(direction, threshold));
    }

    private static TriggerCondition RuntimeCondition(string id, long seconds)
    {
        if (seconds <= 0 || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            throw new LegacyTaskException("condition_threshold_invalid");
        }

        return new TriggerCondition(
            id,
            TriggerConditionKind.Runtime,
            new RuntimeConditionParameters(TimeSpan.FromSeconds(seconds)));
    }

    private static TriggerCondition SystemTimeCondition(string id, string value)
    {
        if (!TimeOnly.TryParseExact(
                value,
                TimeFormats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out TimeOnly time))
        {
            throw new LegacyTaskException("system_time_invalid");
        }

        return new TriggerCondition(
            id,
            TriggerConditionKind.SystemTime,
            new SystemTimeConditionParameters(time));
    }

    private static TriggerAction ParseAction(JsonElement element, LegacySchema schema)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new LegacyTaskException("action_not_object");
        }

        int kind = ReadRequiredInt32(element, "Kind", "action_kind_invalid");
        string value = ReadOptionalString(element, "Value");
        return schema switch
        {
            LegacySchema.Initial => ParseInitialAction(kind, value),
            LegacySchema.Current => ParseCurrentAction(kind, value),
            LegacySchema.Shared when kind == 0 =>
                new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()),
            _ => throw new LegacyTaskException("action_schema_ambiguous"),
        };
    }

    private static TriggerAction ParseInitialAction(int kind, string value)
    {
        return kind switch
        {
            0 => new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()),
            1 => BooleanAction(TriggerActionKind.SetTransparentProxy, value),
            2 => ProxyModeAction(value),
            3 => new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters()),
            4 => NotificationAction(value),
            _ => throw new LegacyTaskException("action_kind_undefined"),
        };
    }

    private static TriggerAction ParseCurrentAction(int kind, string value)
    {
        return kind switch
        {
            0 => new TriggerAction(TriggerActionKind.CloseConnections, new NoActionParameters()),
            1 => BooleanAction(TriggerActionKind.SetLaunchAtStartup, value),
            2 => BooleanAction(TriggerActionKind.SetTransparentProxy, value),
            3 => BooleanAction(TriggerActionKind.SetConnectionSampling, value),
            4 => ProxyModeAction(value),
            5 => new TriggerAction(TriggerActionKind.ExitApplication, new NoActionParameters()),
            6 => NotificationAction(value),
            _ => throw new LegacyTaskException("action_kind_undefined"),
        };
    }

    private static TriggerAction BooleanAction(TriggerActionKind kind, string value)
    {
        if (!bool.TryParse(value, out bool parsed))
        {
            throw new LegacyTaskException("action_boolean_invalid");
        }

        return new TriggerAction(kind, new BooleanActionParameters(parsed));
    }

    private static TriggerAction ProxyModeAction(string value)
    {
        if (!Enum.TryParse(value, ignoreCase: false, out ClashSharpMode mode)
            || !Enum.IsDefined(mode)
            || mode == ClashSharpMode.Faulted)
        {
            throw new LegacyTaskException("action_proxy_mode_invalid");
        }

        return new TriggerAction(
            TriggerActionKind.SwitchProxyMode,
            new ProxyModeActionParameters(mode));
    }

    private static TriggerAction NotificationAction(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new LegacyTaskException("action_notification_invalid");
        }

        return new TriggerAction(
            TriggerActionKind.SendNotification,
            new NotificationActionParameters(value));
    }

    private static TriggerTaskState CreateMigratedState(
        TriggerTaskDefinition definition,
        DateTimeOffset? lastTriggeredAt)
    {
        Dictionary<string, TriggerConditionState> conditions = new(StringComparer.Ordinal);
        foreach (TriggerCondition condition in definition.Conditions)
        {
            TriggerConditionState state = new();
            if (lastTriggeredAt is DateTimeOffset timestamp)
            {
                state = condition.Parameters switch
                {
                    SystemTimeConditionParameters => state with
                    {
                        ConsumedDate = DateOnly.FromDateTime(timestamp.DateTime),
                    },
                    TrafficConditionParameters { Scope: TriggerTrafficScope.AllTime } => state with
                    {
                        ConsumedRevision = definition.Revision,
                    },
                    TrafficConditionParameters or RateConditionParameters or
                        ActiveConnectionsConditionParameters or RuntimeConditionParameters => state with
                        {
                            IsArmed = false,
                        },
                    _ => state,
                };
            }

            conditions.Add(condition.Id, state);
        }

        return new TriggerTaskState(
            definition.Id,
            definition.Revision,
            0,
            conditions,
            lastTriggeredAt);
    }

    private async Task WriteTaskQuarantineAsync(
        string sourceHash,
        IReadOnlyCollection<LegacyTaskQuarantine> tasks,
        CancellationToken cancellationToken)
    {
        string path = BuildArtifactPath("task-quarantine", sourceHash);
        string temporaryPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    new { SourceHash = sourceHash, Tasks = tasks },
                    cancellationToken: cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private async Task MoveCapturedSourceAsync(
        string sourceHash,
        bool quarantineWholeDocument,
        CancellationToken cancellationToken)
    {
        string observedHash = await LegacyTriggerMigrationReader.ComputeHashAsync(
            _legacyPath,
            cancellationToken).ConfigureAwait(false);
        if (!StringComparer.Ordinal.Equals(sourceHash, observedHash))
        {
            throw new IOException("Legacy trigger source changed during migration.");
        }

        string artifactKind = quarantineWholeDocument ? "quarantine" : "migration-backup";
        File.Move(_legacyPath, BuildArtifactPath(artifactKind, sourceHash));
    }

    private string BuildArtifactPath(string artifactKind, string sourceHash)
    {
        string timestamp = _timeProvider.GetUtcNow().ToString(
            "yyyyMMddHHmmssfff",
            CultureInfo.InvariantCulture);
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{_legacyPath}.{artifactKind}.{sourceHash[..12]}.{timestamp}.{Guid.NewGuid():N}");
    }

    private TriggerDiagnostic CreateDiagnostic(
        string code,
        string? taskId,
        string detail)
    {
        return new TriggerDiagnostic(
            code,
            TriggerDiagnosticSeverity.Warning,
            taskId,
            detail,
            _timeProvider.GetUtcNow());
    }

    private TriggerMigrationResult Unavailable(
        TriggerDiagnostic? diagnostic,
        IEnumerable<TriggerDiagnostic>? existing = null)
    {
        List<TriggerDiagnostic> diagnostics = existing?.ToList() ?? [];
        diagnostics.Add(diagnostic ?? CreateDiagnostic(
            "trigger.migration.unavailable",
            null,
            "migration:unavailable"));
        return new TriggerMigrationResult(TriggerMigrationStatus.Unavailable, diagnostics);
    }

    private static LegacySchema DetermineSchema(IEnumerable<JsonElement> tasks)
    {
        bool current = false;
        bool initial = false;
        foreach (JsonElement task in tasks)
        {
            if (task.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (LegacyTriggerMigrationReader.TryGetProperty(task, "Conditions", out JsonElement conditions)
                && conditions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement condition in conditions.EnumerateArray())
                {
                    if (TryReadKind(condition, out int kind))
                    {
                        current |= kind is >= 7 and <= 10;
                        initial |= kind == 6 && !string.IsNullOrEmpty(ReadOptionalString(condition, "Value"));
                    }
                }
            }

            if (LegacyTriggerMigrationReader.TryGetProperty(task, "Actions", out JsonElement actions)
                && actions.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement action in actions.EnumerateArray())
                {
                    if (!TryReadKind(action, out int kind))
                    {
                        continue;
                    }

                    string value = ReadOptionalString(action, "Value");
                    current |= kind is 5 or 6;
                    initial |= kind == 2 && Enum.TryParse<ClashSharpMode>(value, out _)
                        || kind == 3 && string.IsNullOrEmpty(value)
                        || kind == 4 && !Enum.TryParse<ClashSharpMode>(value, out _);
                }
            }
        }

        return (current, initial) switch
        {
            (true, false) => LegacySchema.Current,
            (false, true) => LegacySchema.Initial,
            (false, false) => LegacySchema.Shared,
            _ => LegacySchema.Mixed,
        };
    }

    private static bool TryReadKind(JsonElement element, out int kind)
    {
        kind = default;
        return element.ValueKind == JsonValueKind.Object
            && LegacyTriggerMigrationReader.TryGetProperty(element, "Kind", out JsonElement kindElement)
            && kindElement.ValueKind == JsonValueKind.Number
            && kindElement.TryGetInt32(out kind);
    }

    private static string ReadRequiredString(
        JsonElement element,
        string propertyName,
        string errorCode)
    {
        string? value = TryReadOptionalString(element, propertyName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new LegacyTaskException(errorCode)
            : value;
    }

    private static string? TryReadOptionalString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object
            && LegacyTriggerMigrationReader.TryGetProperty(element, propertyName, out JsonElement value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    private static string ReadOptionalString(JsonElement element, string propertyName)
    {
        if (!LegacyTriggerMigrationReader.TryGetProperty(element, propertyName, out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return string.Empty;
        }

        return value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : throw new LegacyTaskException(propertyName + "_invalid");
    }

    private static bool ReadRequiredBoolean(
        JsonElement element,
        string propertyName,
        string errorCode)
    {
        if (!LegacyTriggerMigrationReader.TryGetProperty(element, propertyName, out JsonElement value)
            || value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new LegacyTaskException(errorCode);
        }

        return value.GetBoolean();
    }

    private static JsonElement ReadRequiredArray(
        JsonElement element,
        string propertyName,
        string errorCode)
    {
        if (!LegacyTriggerMigrationReader.TryGetProperty(element, propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            throw new LegacyTaskException(errorCode);
        }

        return value;
    }

    private static int ReadRequiredInt32(
        JsonElement element,
        string propertyName,
        string errorCode)
    {
        if (!LegacyTriggerMigrationReader.TryGetProperty(element, propertyName, out JsonElement value)
            || value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out int parsed))
        {
            throw new LegacyTaskException(errorCode);
        }

        return parsed;
    }

    private static long ReadOptionalInt64(JsonElement element, string propertyName)
    {
        if (!LegacyTriggerMigrationReader.TryGetProperty(element, propertyName, out JsonElement value))
        {
            return 0;
        }

        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out long parsed)
            ? parsed
            : throw new LegacyTaskException(propertyName + "_invalid");
    }

    private static DateTimeOffset? ReadLastTriggeredAt(JsonElement element)
    {
        if (!LegacyTriggerMigrationReader.TryGetProperty(element, "LastTriggeredAt", out JsonElement value)
            || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                value.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out DateTimeOffset timestamp))
        {
            throw new LegacyTaskException("last_triggered_at_invalid");
        }

        return timestamp;
    }

    private sealed record ParsedLegacyTask(
        int SourceIndex,
        TriggerTaskDefinition Definition,
        DateTimeOffset? LastTriggeredAt);

    private sealed class LegacyTaskException(string errorCode) : Exception(errorCode)
    {
        public string ErrorCode { get; } = errorCode;
    }

    private enum LegacySchema
    {
        Shared,
        Initial,
        Current,
        Mixed,
    }
}
