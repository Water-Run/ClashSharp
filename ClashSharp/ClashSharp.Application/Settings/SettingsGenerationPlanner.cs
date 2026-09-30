using System.Security.Cryptography;
using System.Text;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Prepares a complete replacement authority without carrying runtime evidence from another generation.</summary>
/// <param name="registry">Canonical preference schema.</param>
public sealed class SettingsGenerationPlanner(SettingsRegistry registry)
{
    private readonly SettingsRegistry _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    /// <summary>Preserves unmentioned desired values and queues every replacement setting for independent verification.</summary>
    /// <param name="baseline">Verified immutable source authority.</param>
    /// <param name="changes">Canonical imported preferences or product defaults.</param>
    /// <param name="generationId">Unique candidate identity; batch identities are deterministic within this candidate.</param>
    public SettingsEnvelope Create(SettingsEnvelope baseline, IEnumerable<SettingValueChange> changes, Guid generationId)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(changes);
        if (generationId == Guid.Empty) { throw new ArgumentException("A candidate identity is required.", nameof(generationId)); }
        SettingsEnvelopeValidator validator = new(_registry);
        if (!validator.Validate(baseline).IsValid) { throw new ArgumentException("The source authority is invalid.", nameof(baseline)); }
        Dictionary<SettingKey, SettingDesiredEntry> desired = baseline.Desired.ToDictionary(
            pair => pair.Key, pair => new SettingDesiredEntry(pair.Value.Value, keyDesiredRevision: 1));
        HashSet<SettingKey> touched = [];
        foreach (SettingValueChange change in changes)
        {
            SettingDefinition definition = _registry.Get(change.Key.Value);
            SettingNormalizationResult normalized = definition.Normalize(change.Value.CanonicalText);
            if (!touched.Add(change.Key) || !normalized.IsSuccess || !normalized.Value!.Equals(change.Value))
            {
                throw new ArgumentException("Candidate settings contain duplicate or noncanonical values.", nameof(changes));
            }
            desired[definition.Key] = new(change.Value, keyDesiredRevision: 1);
        }
        Dictionary<SettingKey, SettingAppliedState> applied = desired.Keys.ToDictionary(key => key,
            _ => SettingAppliedState.Unknown(SettingAppliedUnknownReason.NotObserved, SettingAppliedUnknownHandling.QueueApplication));
        List<SettingsApplicationBatch> pending = [];
        foreach (var group in _registry.Definitions.GroupBy(definition => (definition.ApplicationKind, definition.ApplicationTiming))
                     .OrderBy(group => group.Key.ApplicationTiming).ThenBy(group => group.Key.ApplicationKind))
        {
            long sequence = pending.Count + 1;
            pending.Add(new(Identity(generationId, sequence, "batch"),
                group.Key.ApplicationTiming == SettingApplicationTiming.Restart ? SettingsApplicationBatchKind.Restart : SettingsApplicationBatchKind.LiveReconcile,
                sequence, Identity(generationId, sequence, "attempt"), SettingsApplicationBatchState.Pending,
                group.Key.ApplicationKind, group.Select(definition => SettingsApplicationBatchEntry.Create(definition.Key, desired[definition.Key]))));
        }
        SettingsEnvelope candidate = new(SettingsEnvelope.CurrentSchemaVersion, 1, desired, applied, pending, baseline.MigrationHistory);
        if (!validator.Validate(candidate).IsValid) { throw new InvalidOperationException("The replacement authority failed validation."); }
        return candidate;
    }

    private static Guid Identity(Guid candidate, long sequence, string purpose) => new(SHA256.HashData(Encoding.UTF8.GetBytes(
        FormattableString.Invariant($"clashsharp-settings-generation-v1/{candidate:N}/{sequence}/{purpose}"))).AsSpan(0, 16));
}
