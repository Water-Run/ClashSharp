namespace ClashSharp.Settings;

/// <summary>Describes one stable settings-envelope validation failure.</summary>
public sealed record SettingsEnvelopeValidationError
{
    /// <summary>Initializes one path-addressed validation failure.</summary>
    /// <param name="code">Stable nonlocalized diagnostic code.</param>
    /// <param name="path">Stable envelope path.</param>
    public SettingsEnvelopeValidationError(string code, string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Code = code;
        Path = path;
    }

    /// <summary>Gets the stable nonlocalized diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the stable envelope path.</summary>
    public string Path { get; }
}

/// <summary>Contains all failures from one deterministic envelope validation pass.</summary>
public sealed class SettingsEnvelopeValidationResult
{
    internal SettingsEnvelopeValidationResult(IEnumerable<SettingsEnvelopeValidationError> errors)
    {
        SettingsEnvelopeValidationError[] snapshot = errors.ToArray();
        Errors = Array.AsReadOnly(snapshot);
    }

    /// <summary>Gets whether the envelope satisfies every registry and partition invariant.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Gets all stable validation failures in deterministic traversal order.</summary>
    public IReadOnlyList<SettingsEnvelopeValidationError> Errors { get; }
}

/// <summary>
/// Validates complete registry coverage, canonical values, applied evidence, and pending partition identity.
/// </summary>
public sealed class SettingsEnvelopeValidator
{
    private readonly SettingsRegistry _registry;

    /// <summary>Initializes a validator against one immutable canonical registry.</summary>
    /// <param name="registry">Canonical settings registry.</param>
    public SettingsEnvelopeValidator(SettingsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
    }

    /// <summary>Validates an immutable envelope without mutating it.</summary>
    /// <param name="envelope">Envelope to validate.</param>
    /// <returns>All stable validation errors, or a successful empty result.</returns>
    public SettingsEnvelopeValidationResult Validate(SettingsEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        List<SettingsEnvelopeValidationError> errors = [];

        if (envelope.SchemaVersion != SettingsEnvelope.CurrentSchemaVersion)
        {
            Add(
                errors,
                "settings.envelope.schema.unsupported",
                "schemaVersion");
        }

        ValidateMapCoverage(envelope, errors);
        ValidateCanonicalValues(envelope, errors);
        Dictionary<SettingKey, int> pendingCoverage = ValidateBatches(envelope, errors);
        ValidatePendingCoverage(envelope, pendingCoverage, errors);
        ValidateMigrationHistory(envelope, errors);

        return new SettingsEnvelopeValidationResult(errors);
    }

    private void ValidateMapCoverage(
        SettingsEnvelope envelope,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        foreach (SettingDefinition definition in _registry.Definitions)
        {
            if (!envelope.Desired.ContainsKey(definition.Key))
            {
                Add(
                    errors,
                    "settings.envelope.desired.missing",
                    $"desired.{definition.Key.Value}");
            }

            if (!envelope.Applied.ContainsKey(definition.Key))
            {
                Add(
                    errors,
                    "settings.envelope.applied.missing",
                    $"applied.{definition.Key.Value}");
            }
        }

        ValidateMapKeys(envelope.Desired.Keys, "desired", errors);
        ValidateMapKeys(envelope.Applied.Keys, "applied", errors);
    }

    private void ValidateMapKeys(
        IEnumerable<SettingKey> keys,
        string mapName,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        foreach (SettingKey key in keys.OrderBy(static key => key.Value, StringComparer.Ordinal))
        {
            if (!_registry.TryResolve(
                    key.Value,
                    out SettingDefinition? definition,
                    out SettingKeyResolution resolution))
            {
                Add(
                    errors,
                    "settings.envelope.key.unregistered",
                    $"{mapName}.{key.Value}");
            }
            else if (resolution != SettingKeyResolution.Canonical
                || definition!.Key != key)
            {
                Add(
                    errors,
                    "settings.envelope.key.alias_not_canonical",
                    $"{mapName}.{key.Value}");
            }
        }
    }

    private void ValidateCanonicalValues(
        SettingsEnvelope envelope,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        foreach (SettingDefinition definition in _registry.Definitions)
        {
            if (envelope.Desired.TryGetValue(
                    definition.Key,
                    out SettingDesiredEntry? desired))
            {
                if (desired.KeyDesiredRevision > envelope.EnvelopeRevision)
                {
                    Add(
                        errors,
                        "settings.envelope.desired.revision_ahead",
                        $"desired.{definition.Key.Value}.keyDesiredRevision");
                }

                ValidateValue(
                    definition,
                    desired.Value,
                    $"desired.{definition.Key.Value}.value",
                    errors);
            }

            if (!envelope.Applied.TryGetValue(
                    definition.Key,
                    out SettingAppliedState? applied))
            {
                continue;
            }

            if (applied.Kind == SettingAppliedStateKind.Verified)
            {
                ValidateVerifiedApplied(definition, applied, errors);
            }
            else if (applied.UnknownReason == SettingAppliedUnknownReason.BlockedProbe
                && definition.Authority != SettingAuthority.ExternallyObserved)
            {
                Add(
                    errors,
                    "settings.envelope.applied.blocked_probe_not_external",
                    $"applied.{definition.Key.Value}.unknownReason");
            }
        }
    }

    private static void ValidateValue(
        SettingDefinition definition,
        SettingValue value,
        string path,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        SettingNormalizationResult normalized = definition.Normalize(value.CanonicalText);
        if (!normalized.IsSuccess || !value.Equals(normalized.Value))
        {
            Add(errors, "settings.envelope.value.invalid", path);
        }
    }

    private static void ValidateVerifiedApplied(
        SettingDefinition definition,
        SettingAppliedState applied,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        string path = $"applied.{definition.Key.Value}";
        ValidateValue(definition, applied.Value!, $"{path}.value", errors);

        string expectedHash =
            SettingsApplicationBatchEntry.ComputeValueHash(applied.Value!);
        if (!StringComparer.Ordinal.Equals(expectedHash, applied.ObservedHash))
        {
            Add(
                errors,
                "settings.envelope.applied.hash_mismatch",
                $"{path}.observedHash");
        }
    }

    private Dictionary<SettingKey, int> ValidateBatches(
        SettingsEnvelope envelope,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        HashSet<Guid> batchIds = [];
        HashSet<Guid> attemptIds = [];
        HashSet<long> creationSequences = [];
        Dictionary<SettingKey, int> coverage = [];

        for (int index = 0; index < envelope.PendingApplications.Count; index++)
        {
            SettingsApplicationBatch batch = envelope.PendingApplications[index];
            string batchPath = $"pendingApplications[{index}]";
            if (index > 0
                && SettingsApplicationBatchComparer.Instance.Compare(
                    envelope.PendingApplications[index - 1],
                    batch) >= 0)
            {
                Add(
                    errors,
                    "settings.envelope.batches.not_ordered",
                    batchPath);
            }

            AddDuplicateError(
                batchIds.Add(batch.BatchId),
                "settings.envelope.batch.id_duplicate",
                $"{batchPath}.batchId",
                errors);
            AddDuplicateError(
                attemptIds.Add(batch.AttemptId),
                "settings.envelope.batch.attempt_duplicate",
                $"{batchPath}.attemptId",
                errors);
            AddDuplicateError(
                creationSequences.Add(batch.CreationSequence),
                "settings.envelope.batch.sequence_duplicate",
                $"{batchPath}.creationSequence",
                errors);

            for (int entryIndex = 0; entryIndex < batch.Entries.Count; entryIndex++)
            {
                ValidateBatchEntry(
                    envelope,
                    batch,
                    batch.Entries[entryIndex],
                    $"{batchPath}.entries[{entryIndex}]",
                    coverage,
                    errors);
            }
        }

        return coverage;
    }

    private void ValidateBatchEntry(
        SettingsEnvelope envelope,
        SettingsApplicationBatch batch,
        SettingsApplicationBatchEntry entry,
        string path,
        IDictionary<SettingKey, int> coverage,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        coverage.TryGetValue(entry.Key, out int priorCoverage);
        coverage[entry.Key] = priorCoverage + 1;
        if (coverage[entry.Key] > 1)
        {
            Add(errors, "settings.envelope.pending.overlap", $"{path}.key");
        }

        if (!_registry.TryResolve(
                entry.Key.Value,
                out SettingDefinition? definition,
                out SettingKeyResolution resolution)
            || resolution != SettingKeyResolution.Canonical)
        {
            Add(errors, "settings.envelope.batch.entry.unregistered", $"{path}.key");
            return;
        }

        SettingsApplicationBatchKind expectedKind =
            definition!.ApplicationTiming == SettingApplicationTiming.Restart
                ? SettingsApplicationBatchKind.Restart
                : SettingsApplicationBatchKind.LiveReconcile;
        if (batch.Kind != expectedKind)
        {
            Add(errors, "settings.envelope.batch.kind_mismatch", $"{path}.key");
        }

        if (batch.ApplicationKind != definition.ApplicationKind)
        {
            Add(
                errors,
                "settings.envelope.batch.application_mismatch",
                $"{path}.key");
        }

        if (!envelope.Desired.TryGetValue(entry.Key, out SettingDesiredEntry? desired))
        {
            Add(
                errors,
                "settings.envelope.batch.entry.desired_missing",
                $"{path}.key");
            return;
        }

        if (entry.KeyDesiredRevision != desired.KeyDesiredRevision)
        {
            Add(
                errors,
                "settings.envelope.batch.entry.stale_revision",
                $"{path}.keyDesiredRevision");
        }

        string expectedHash =
            SettingsApplicationBatchEntry.ComputeValueHash(desired.Value);
        if (!StringComparer.Ordinal.Equals(expectedHash, entry.ValueHash))
        {
            Add(
                errors,
                "settings.envelope.batch.entry.stale_hash",
                $"{path}.valueHash");
        }
    }

    private void ValidatePendingCoverage(
        SettingsEnvelope envelope,
        IReadOnlyDictionary<SettingKey, int> coverage,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        foreach (SettingDefinition definition in _registry.Definitions)
        {
            if (!envelope.Desired.TryGetValue(
                    definition.Key,
                    out SettingDesiredEntry? desired)
                || !envelope.Applied.TryGetValue(
                    definition.Key,
                    out SettingAppliedState? applied))
            {
                continue;
            }

            bool requiresPending = applied.Kind switch
            {
                SettingAppliedStateKind.Verified => !desired.Value.Equals(applied.Value),
                SettingAppliedStateKind.Unknown =>
                    applied.RequiresApplication
                    || definition.Authority != SettingAuthority.ExternallyObserved,
                _ => false,
            };
            int count = coverage.GetValueOrDefault(definition.Key);
            if (requiresPending && count == 0)
            {
                Add(
                    errors,
                    "settings.envelope.pending.uncovered",
                    $"pendingApplications.{definition.Key.Value}");
            }
            else if (!requiresPending && count > 0)
            {
                Add(
                    errors,
                    "settings.envelope.pending.unexpected",
                    $"pendingApplications.{definition.Key.Value}");
            }
        }
    }

    private static void ValidateMigrationHistory(
        SettingsEnvelope envelope,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        HashSet<Guid> migrationIds = [];
        int? priorTarget = null;

        for (int index = 0; index < envelope.MigrationHistory.Count; index++)
        {
            SettingsMigrationRecord migration = envelope.MigrationHistory[index];
            string path = $"migrationHistory[{index}]";
            AddDuplicateError(
                migrationIds.Add(migration.MigrationId),
                "settings.envelope.migration.id_duplicate",
                $"{path}.migrationId",
                errors);

            if (migration.ToSchemaVersion > envelope.SchemaVersion)
            {
                Add(
                    errors,
                    "settings.envelope.migration.target_ahead",
                    $"{path}.toSchemaVersion");
            }

            if (priorTarget is not null
                && migration.FromSchemaVersion != priorTarget.Value)
            {
                Add(
                    errors,
                    "settings.envelope.migration.not_contiguous",
                    $"{path}.fromSchemaVersion");
            }

            priorTarget = migration.ToSchemaVersion;
        }
    }

    private static void AddDuplicateError(
        bool wasAdded,
        string code,
        string path,
        ICollection<SettingsEnvelopeValidationError> errors)
    {
        if (!wasAdded)
        {
            Add(errors, code, path);
        }
    }

    private static void Add(
        ICollection<SettingsEnvelopeValidationError> errors,
        string code,
        string path) =>
        errors.Add(new SettingsEnvelopeValidationError(code, path));
}
