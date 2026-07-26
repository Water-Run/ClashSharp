using System.Collections.ObjectModel;

namespace ClashSharp.Settings;

/// <summary>
/// Contains one immutable versioned settings transaction state: desired, applied, pending, and migrations.
/// </summary>
public sealed class SettingsEnvelope
{
    /// <summary>Gets the current settings-envelope schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Initializes an immutable settings envelope with defensive collection snapshots.</summary>
    /// <param name="schemaVersion">Positive document schema version.</param>
    /// <param name="envelopeRevision">Positive revision advanced by every envelope transaction.</param>
    /// <param name="desired">Canonical desired entry map.</param>
    /// <param name="applied">Canonical effective-state map.</param>
    /// <param name="pendingApplications">Ordered pending application batches.</param>
    /// <param name="migrationHistory">Ordered migration history.</param>
    public SettingsEnvelope(
        int schemaVersion,
        long envelopeRevision,
        IEnumerable<KeyValuePair<SettingKey, SettingDesiredEntry>> desired,
        IEnumerable<KeyValuePair<SettingKey, SettingAppliedState>> applied,
        IEnumerable<SettingsApplicationBatch> pendingApplications,
        IEnumerable<SettingsMigrationRecord> migrationHistory)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(schemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(envelopeRevision);

        SchemaVersion = schemaVersion;
        EnvelopeRevision = envelopeRevision;
        Desired = CopyMap(desired, nameof(desired));
        Applied = CopyMap(applied, nameof(applied));
        PendingApplications = CopyList(pendingApplications, nameof(pendingApplications));
        MigrationHistory = CopyList(migrationHistory, nameof(migrationHistory));
    }

    /// <summary>Gets the document schema version.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the document transaction revision.</summary>
    public long EnvelopeRevision { get; }

    /// <summary>Gets the complete canonical desired entry map.</summary>
    public IReadOnlyDictionary<SettingKey, SettingDesiredEntry> Desired { get; }

    /// <summary>Gets the complete canonical effective-state map.</summary>
    public IReadOnlyDictionary<SettingKey, SettingAppliedState> Applied { get; }

    /// <summary>Gets pending batches in total processing order.</summary>
    public IReadOnlyList<SettingsApplicationBatch> PendingApplications { get; }

    /// <summary>Gets migration records in durable history order.</summary>
    public IReadOnlyList<SettingsMigrationRecord> MigrationHistory { get; }

    private static IReadOnlyDictionary<SettingKey, TValue> CopyMap<TValue>(
        IEnumerable<KeyValuePair<SettingKey, TValue>> source,
        string parameterName)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        Dictionary<SettingKey, TValue> snapshot = [];

        foreach ((SettingKey key, TValue value) in source)
        {
            if (key is null || value is null)
            {
                throw new ArgumentException(
                    "Settings envelope maps cannot contain null keys or values.",
                    parameterName);
            }

            if (!snapshot.TryAdd(key, value))
            {
                throw new ArgumentException(
                    $"Settings envelope map contains duplicate key '{key.Value}'.",
                    parameterName);
            }
        }

        return new ReadOnlyDictionary<SettingKey, TValue>(snapshot);
    }

    private static IReadOnlyList<TValue> CopyList<TValue>(
        IEnumerable<TValue> source,
        string parameterName)
        where TValue : class
    {
        ArgumentNullException.ThrowIfNull(source, parameterName);
        TValue[] snapshot = source.ToArray();
        if (snapshot.Any(static value => value is null))
        {
            throw new ArgumentException(
                "Settings envelope collections cannot contain null.",
                parameterName);
        }

        return Array.AsReadOnly(snapshot);
    }
}
