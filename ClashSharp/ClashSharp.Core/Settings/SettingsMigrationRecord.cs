namespace ClashSharp.Settings;

/// <summary>Records one idempotent source-hash-bound settings schema migration.</summary>
public sealed record SettingsMigrationRecord
{
    /// <summary>Initializes an immutable migration history record.</summary>
    /// <param name="migrationId">Stable nonempty migration identity.</param>
    /// <param name="fromSchemaVersion">Source schema version; zero represents an unversioned legacy source.</param>
    /// <param name="toSchemaVersion">Positive target schema version greater than the source.</param>
    /// <param name="sourceHash">Canonical lowercase SHA-256 source snapshot hash.</param>
    public SettingsMigrationRecord(
        Guid migrationId,
        int fromSchemaVersion,
        int toSchemaVersion,
        string sourceHash)
    {
        if (migrationId == Guid.Empty)
        {
            throw new ArgumentException("Migration identity cannot be empty.", nameof(migrationId));
        }

        ArgumentOutOfRangeException.ThrowIfNegative(fromSchemaVersion);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(toSchemaVersion);
        if (toSchemaVersion <= fromSchemaVersion)
        {
            throw new ArgumentException(
                "Target schema version must exceed the source version.",
                nameof(toSchemaVersion));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHash);
        if (!SettingsValueHash.IsCanonicalSha256(sourceHash))
        {
            throw new ArgumentException(
                "Source hash must be a canonical lowercase SHA-256 value.",
                nameof(sourceHash));
        }

        MigrationId = migrationId;
        FromSchemaVersion = fromSchemaVersion;
        ToSchemaVersion = toSchemaVersion;
        SourceHash = sourceHash;
    }

    /// <summary>Gets the stable migration identity.</summary>
    public Guid MigrationId { get; }

    /// <summary>Gets the source schema version.</summary>
    public int FromSchemaVersion { get; }

    /// <summary>Gets the target schema version.</summary>
    public int ToSchemaVersion { get; }

    /// <summary>Gets the canonical source snapshot hash.</summary>
    public string SourceHash { get; }
}
