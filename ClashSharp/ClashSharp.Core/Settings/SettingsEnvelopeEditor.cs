using System.Security.Cryptography;
using System.Text;

namespace ClashSharp.Settings;

/// <summary>Contains one canonical typed setting value change.</summary>
public sealed record SettingValueChange
{
    /// <summary>Initializes one canonical typed value change.</summary>
    /// <param name="key">Canonical writable setting key.</param>
    /// <param name="value">Registry-normalized target value.</param>
    public SettingValueChange(SettingKey key, SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(value);
        Key = key;
        Value = value;
    }

    /// <summary>Gets the canonical writable setting key.</summary>
    public SettingKey Key { get; }

    /// <summary>Gets the registry-normalized target value.</summary>
    public SettingValue Value { get; }
}

/// <summary>Classifies the result of one pure settings-envelope edit.</summary>
public enum SettingsEnvelopeEditOutcome
{
    /// <summary>The envelope was rewritten atomically.</summary>
    Updated = 0,

    /// <summary>Every requested target already matched desired state.</summary>
    NoChange = 1,

    /// <summary>A touched running batch cannot be split safely.</summary>
    Busy = 2,

    /// <summary>The source envelope, key, or value was invalid.</summary>
    Invalid = 3,
}

/// <summary>Contains an edited envelope or a stable non-mutating failure.</summary>
public sealed class SettingsEnvelopeEditResult
{
    private SettingsEnvelopeEditResult(
        SettingsEnvelope envelope,
        SettingsEnvelopeEditOutcome outcome,
        string? errorCode,
        string? errorPath)
    {
        Envelope = envelope;
        Outcome = outcome;
        ErrorCode = errorCode;
        ErrorPath = errorPath;
    }

    /// <summary>Gets the resulting envelope, or the original instance on failure/no-op.</summary>
    public SettingsEnvelope Envelope { get; }

    /// <summary>Gets the edit outcome.</summary>
    public SettingsEnvelopeEditOutcome Outcome { get; }

    /// <summary>Gets whether the edit succeeded or was an exact no-op.</summary>
    public bool IsSuccess =>
        Outcome is SettingsEnvelopeEditOutcome.Updated
            or SettingsEnvelopeEditOutcome.NoChange;

    /// <summary>Gets the stable nonlocalized failure code.</summary>
    public string? ErrorCode { get; }

    /// <summary>Gets the stable failure path when available.</summary>
    public string? ErrorPath { get; }

    internal static SettingsEnvelopeEditResult Updated(SettingsEnvelope envelope) =>
        new(envelope, SettingsEnvelopeEditOutcome.Updated, null, null);

    internal static SettingsEnvelopeEditResult NoChange(SettingsEnvelope envelope) =>
        new(envelope, SettingsEnvelopeEditOutcome.NoChange, null, null);

    internal static SettingsEnvelopeEditResult Busy(
        SettingsEnvelope envelope,
        string path) =>
        new(
            envelope,
            SettingsEnvelopeEditOutcome.Busy,
            "settings.envelope.edit.running_batch",
            path);

    internal static SettingsEnvelopeEditResult Invalid(
        SettingsEnvelope envelope,
        string code,
        string? path = null) =>
        new(envelope, SettingsEnvelopeEditOutcome.Invalid, code, path);
}

/// <summary>
/// Performs deterministic pure change and revert rewrites while preserving unrelated batch identity.
/// </summary>
public sealed class SettingsEnvelopeEditor
{
    private readonly SettingsRegistry _registry;
    private readonly SettingsEnvelopeValidator _validator;

    /// <summary>Initializes an editor against one immutable canonical registry.</summary>
    /// <param name="registry">Canonical settings registry.</param>
    public SettingsEnvelopeEditor(SettingsRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        _registry = registry;
        _validator = new SettingsEnvelopeValidator(registry);
    }

    /// <summary>
    /// Atomically applies canonical desired changes and repartitions only the touched pending entries.
    /// </summary>
    /// <param name="envelope">Valid immutable source envelope.</param>
    /// <param name="changes">Canonical typed changes, unique by key.</param>
    /// <param name="transactionId">Nonempty caller-owned identity used to derive new batch attempts.</param>
    /// <returns>An updated envelope, exact no-op, typed busy result, or typed invalid result.</returns>
    public SettingsEnvelopeEditResult ApplyChanges(
        SettingsEnvelope envelope,
        IEnumerable<SettingValueChange> changes,
        Guid transactionId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(changes);

        SettingsEnvelopeEditResult? invalidSource = ValidateSource(envelope);
        if (invalidSource is not null)
        {
            return invalidSource;
        }

        if (transactionId == Guid.Empty)
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.transaction_id_empty",
                "transactionId");
        }

        SettingValueChange[] snapshot = changes.ToArray();
        if (snapshot.Any(static change => change is null))
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.change_null",
                "changes");
        }

        Dictionary<SettingKey, SettingValue> requested = [];
        foreach (SettingValueChange change in snapshot)
        {
            SettingsEnvelopeEditResult? invalidChange =
                ValidateChange(envelope, change, requested);
            if (invalidChange is not null)
            {
                return invalidChange;
            }
        }

        Dictionary<SettingKey, SettingValue> changed = requested
            .Where(pair => !envelope.Desired[pair.Key].Value.Equals(pair.Value))
            .ToDictionary();
        if (changed.Count == 0)
        {
            return SettingsEnvelopeEditResult.NoChange(envelope);
        }

        HashSet<SettingKey> changedKeys = [.. changed.Keys];
        SettingsApplicationBatch? running = envelope.PendingApplications.FirstOrDefault(
            batch => batch.State == SettingsApplicationBatchState.Running
                && batch.Entries.Any(entry => changedKeys.Contains(entry.Key)));
        if (running is not null)
        {
            return SettingsEnvelopeEditResult.Busy(
                envelope,
                $"pendingApplications.{running.BatchId:D}");
        }

        try
        {
            return Rewrite(envelope, changed, changedKeys, transactionId);
        }
        catch (OverflowException)
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.revision_overflow");
        }
    }

    /// <summary>
    /// Reverts keys to verified applied values or their explicit registry safe fallbacks when unknown.
    /// </summary>
    /// <param name="envelope">Valid immutable source envelope.</param>
    /// <param name="keys">Canonical keys to revert, unique by key.</param>
    /// <param name="transactionId">Nonempty caller-owned identity used to derive new batch attempts.</param>
    /// <returns>An updated envelope, exact no-op, typed busy result, or typed invalid result.</returns>
    public SettingsEnvelopeEditResult Revert(
        SettingsEnvelope envelope,
        IEnumerable<SettingKey> keys,
        Guid transactionId)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(keys);

        SettingsEnvelopeEditResult? invalidSource = ValidateSource(envelope);
        if (invalidSource is not null)
        {
            return invalidSource;
        }

        SettingKey[] snapshot = keys.ToArray();
        if (snapshot.Any(static key => key is null))
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.key_null",
                "keys");
        }

        HashSet<SettingKey> seen = [];
        List<SettingValueChange> changes = [];
        foreach (SettingKey key in snapshot)
        {
            if (!seen.Add(key))
            {
                return SettingsEnvelopeEditResult.Invalid(
                    envelope,
                    "settings.envelope.edit.duplicate_key",
                    $"keys.{key.Value}");
            }

            if (!_registry.TryResolve(
                    key.Value,
                    out SettingDefinition? definition,
                    out SettingKeyResolution resolution))
            {
                return SettingsEnvelopeEditResult.Invalid(
                    envelope,
                    "settings.envelope.edit.key_unregistered",
                    $"keys.{key.Value}");
            }

            if (resolution != SettingKeyResolution.Canonical)
            {
                return SettingsEnvelopeEditResult.Invalid(
                    envelope,
                    "settings.envelope.edit.alias_read_only",
                    $"keys.{key.Value}");
            }

            SettingAppliedState applied = envelope.Applied[definition!.Key];
            SettingValue target = applied.Kind == SettingAppliedStateKind.Verified
                ? applied.Value!
                : definition.SafeFallback;
            changes.Add(new SettingValueChange(definition.Key, target));
        }

        return ApplyChanges(envelope, changes, transactionId);
    }

    private SettingsEnvelopeEditResult Rewrite(
        SettingsEnvelope envelope,
        IReadOnlyDictionary<SettingKey, SettingValue> changed,
        IReadOnlySet<SettingKey> changedKeys,
        Guid transactionId)
    {
        Dictionary<SettingKey, SettingDesiredEntry> desired =
            envelope.Desired.ToDictionary();
        Dictionary<BatchGroup, List<SettingsApplicationBatchEntry>> newEntries = [];

        foreach ((SettingKey key, SettingValue value) in changed.OrderBy(
                     static pair => pair.Key.Value,
                     StringComparer.Ordinal))
        {
            SettingDesiredEntry prior = envelope.Desired[key];
            SettingDesiredEntry next = new(
                value,
                checked(prior.KeyDesiredRevision + 1));
            desired[key] = next;

            SettingAppliedState applied = envelope.Applied[key];
            bool requiresPending = applied.Kind switch
            {
                SettingAppliedStateKind.Verified => !value.Equals(applied.Value),
                SettingAppliedStateKind.Unknown => applied.RequiresApplication,
                _ => false,
            };
            if (!requiresPending)
            {
                continue;
            }

            SettingDefinition definition = _registry.Get(key.Value);
            BatchGroup group = new(
                definition.ApplicationTiming == SettingApplicationTiming.Restart
                    ? SettingsApplicationBatchKind.Restart
                    : SettingsApplicationBatchKind.LiveReconcile,
                definition.ApplicationKind);
            if (!newEntries.TryGetValue(
                    group,
                    out List<SettingsApplicationBatchEntry>? groupEntries))
            {
                groupEntries = [];
                newEntries.Add(group, groupEntries);
            }

            groupEntries.Add(SettingsApplicationBatchEntry.Create(key, next));
        }

        List<SettingsApplicationBatch> batches = SplitExistingBatches(
            envelope.PendingApplications,
            changedKeys);
        HashSet<Guid> reservedBatchIds =
            envelope.PendingApplications.Select(static batch => batch.BatchId).ToHashSet();
        HashSet<Guid> reservedAttemptIds =
            envelope.PendingApplications.Select(static batch => batch.AttemptId).ToHashSet();
        long creationSequenceHighWater = envelope.PendingApplications.Count == 0
            ? 0
            : envelope.PendingApplications.Max(static batch => batch.CreationSequence);
        if (!AppendNewBatches(
                batches,
                newEntries,
                transactionId,
                creationSequenceHighWater,
                reservedBatchIds,
                reservedAttemptIds))
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.identity_collision");
        }

        batches.Sort(SettingsApplicationBatchComparer.Instance);

        SettingsEnvelope rewritten = new(
            envelope.SchemaVersion,
            checked(envelope.EnvelopeRevision + 1),
            desired,
            envelope.Applied,
            batches,
            envelope.MigrationHistory);
        SettingsEnvelopeValidationResult validation = _validator.Validate(rewritten);
        if (!validation.IsValid)
        {
            SettingsEnvelopeValidationError first = validation.Errors[0];
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.result_invalid",
                $"{first.Code}:{first.Path}");
        }

        return SettingsEnvelopeEditResult.Updated(rewritten);
    }

    private static List<SettingsApplicationBatch> SplitExistingBatches(
        IEnumerable<SettingsApplicationBatch> source,
        IReadOnlySet<SettingKey> changedKeys)
    {
        List<SettingsApplicationBatch> result = [];
        foreach (SettingsApplicationBatch batch in source)
        {
            SettingsApplicationBatchEntry[] retained = batch.Entries
                .Where(entry => !changedKeys.Contains(entry.Key))
                .ToArray();
            if (retained.Length == batch.Entries.Count)
            {
                result.Add(batch);
            }
            else if (retained.Length > 0)
            {
                result.Add(batch.WithEntries(retained));
            }
        }

        return result;
    }

    private static bool AppendNewBatches(
        ICollection<SettingsApplicationBatch> batches,
        IReadOnlyDictionary<BatchGroup, List<SettingsApplicationBatchEntry>> groups,
        Guid transactionId,
        long creationSequenceHighWater,
        IReadOnlySet<Guid> reservedBatchIds,
        IReadOnlySet<Guid> reservedAttemptIds)
    {
        long sequence = creationSequenceHighWater;
        HashSet<Guid> batchIds = [.. reservedBatchIds];
        HashSet<Guid> attemptIds = [.. reservedAttemptIds];

        foreach ((BatchGroup group, List<SettingsApplicationBatchEntry> entries) in groups
                     .OrderBy(static pair => pair.Key.Kind)
                     .ThenBy(static pair => pair.Key.ApplicationKind))
        {
            sequence = checked(sequence + 1);
            string identitySuffix =
                $"{(int)group.Kind}:{(int)group.ApplicationKind}:{sequence}";
            Guid batchId = CreateDeterministicId(
                transactionId,
                $"batch:{identitySuffix}");
            Guid attemptId = CreateDeterministicId(
                transactionId,
                $"attempt:{identitySuffix}");
            if (!batchIds.Add(batchId) || !attemptIds.Add(attemptId))
            {
                return false;
            }

            batches.Add(
                new SettingsApplicationBatch(
                    batchId,
                    group.Kind,
                    sequence,
                    attemptId,
                    SettingsApplicationBatchState.Pending,
                    group.ApplicationKind,
                    entries));
        }

        return true;
    }

    private SettingsEnvelopeEditResult? ValidateSource(SettingsEnvelope envelope)
    {
        SettingsEnvelopeValidationResult validation = _validator.Validate(envelope);
        if (validation.IsValid)
        {
            return null;
        }

        SettingsEnvelopeValidationError first = validation.Errors[0];
        return SettingsEnvelopeEditResult.Invalid(
            envelope,
            "settings.envelope.edit.source_invalid",
            $"{first.Code}:{first.Path}");
    }

    private SettingsEnvelopeEditResult? ValidateChange(
        SettingsEnvelope envelope,
        SettingValueChange change,
        IDictionary<SettingKey, SettingValue> requested)
    {
        if (!_registry.TryResolve(
                change.Key.Value,
                out SettingDefinition? definition,
                out SettingKeyResolution resolution))
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.key_unregistered",
                $"changes.{change.Key.Value}");
        }

        if (resolution != SettingKeyResolution.Canonical)
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.alias_read_only",
                $"changes.{change.Key.Value}");
        }

        if (!requested.TryAdd(definition!.Key, change.Value))
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.duplicate_key",
                $"changes.{definition.Key.Value}");
        }

        SettingNormalizationResult normalized =
            definition.Normalize(change.Value.CanonicalText);
        if (!normalized.IsSuccess || !change.Value.Equals(normalized.Value))
        {
            return SettingsEnvelopeEditResult.Invalid(
                envelope,
                "settings.envelope.edit.value_invalid",
                $"changes.{definition.Key.Value}.value");
        }

        return null;
    }

    private static Guid CreateDeterministicId(Guid transactionId, string purpose)
    {
        byte[] namespaceBytes = transactionId.ToByteArray();
        byte[] purposeBytes = Encoding.UTF8.GetBytes(purpose);
        byte[] input = new byte[namespaceBytes.Length + purposeBytes.Length];
        namespaceBytes.CopyTo(input, 0);
        purposeBytes.CopyTo(input, namespaceBytes.Length);
        byte[] hash = SHA256.HashData(input);
        byte[] guidBytes = hash[..16];
        guidBytes[7] = (byte)((guidBytes[7] & 0x0F) | 0x80);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);
        return new Guid(guidBytes);
    }

    private readonly record struct BatchGroup(
        SettingsApplicationBatchKind Kind,
        SettingApplicationKind ApplicationKind);
}
