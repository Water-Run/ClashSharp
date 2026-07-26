namespace ClashSharp.Settings;

/// <summary>Discriminates verified effective state from explicitly unknown effective state.</summary>
public enum SettingAppliedStateKind
{
    /// <summary>The effective value was observed and verified.</summary>
    Verified = 0,

    /// <summary>The effective value cannot currently be established safely.</summary>
    Unknown = 1,
}

/// <summary>Identifies the evidence source for a verified effective setting value.</summary>
public enum SettingAppliedValueSource
{
    /// <summary>A newly initialized generation verified its default value.</summary>
    DefaultInitialization = 0,

    /// <summary>A migration validated the effective legacy value.</summary>
    LegacyMigration = 1,

    /// <summary>An external or runtime probe observed the value.</summary>
    RuntimeProbe = 2,

    /// <summary>A mutation participant applied and verified the value.</summary>
    MutationVerification = 3,

    /// <summary>Startup reconciliation applied and verified pending work.</summary>
    StartupReconciliation = 4,
}

/// <summary>Classifies why an effective setting value is unknown.</summary>
public enum SettingAppliedUnknownReason
{
    /// <summary>No trustworthy observation has been performed yet.</summary>
    NotObserved = 0,

    /// <summary>The required observation failed.</summary>
    ProbeFailed = 1,

    /// <summary>The probe is deliberately blocked and automatic application is unsafe.</summary>
    BlockedProbe = 2,

    /// <summary>Persisted applied-state evidence was invalid or incompatible.</summary>
    InvalidPersistedState = 3,
}

/// <summary>Declares the safe runtime and reconciliation behavior while effective state is unknown.</summary>
public enum SettingAppliedUnknownHandling
{
    /// <summary>Retain the safe baseline and cover the desired value with one pending application.</summary>
    QueueApplication = 0,

    /// <summary>Use the registry safe fallback locally without scheduling an automatic application.</summary>
    UseSafeFallback = 1,

    /// <summary>Disable the affected operation without scheduling an automatic application.</summary>
    BlockOperation = 2,
}

/// <summary>
/// Represents either verified effective setting evidence or an explicit unknown state with safe handling.
/// </summary>
public sealed class SettingAppliedState : IEquatable<SettingAppliedState>
{
    private SettingAppliedState(
        SettingAppliedStateKind kind,
        SettingValue? value,
        SettingAppliedValueSource? source,
        string? observedHash,
        DateTimeOffset? observedAt,
        SettingAppliedUnknownReason? unknownReason,
        SettingAppliedUnknownHandling? unknownHandling)
    {
        Kind = kind;
        Value = value;
        Source = source;
        ObservedHash = observedHash;
        ObservedAt = observedAt;
        UnknownReason = unknownReason;
        UnknownHandling = unknownHandling;
    }

    /// <summary>Gets whether this state is verified or unknown.</summary>
    public SettingAppliedStateKind Kind { get; }

    /// <summary>Gets the verified canonical value, or null for unknown state.</summary>
    public SettingValue? Value { get; }

    /// <summary>Gets the evidence source, or null for unknown state.</summary>
    public SettingAppliedValueSource? Source { get; }

    /// <summary>Gets the SHA-256 hash of the verified canonical value, or null for unknown state.</summary>
    public string? ObservedHash { get; }

    /// <summary>Gets the UTC observation timestamp, or null for unknown state.</summary>
    public DateTimeOffset? ObservedAt { get; }

    /// <summary>Gets the reason effective state is unknown, or null for verified state.</summary>
    public SettingAppliedUnknownReason? UnknownReason { get; }

    /// <summary>Gets the safe handling while state is unknown, or null for verified state.</summary>
    public SettingAppliedUnknownHandling? UnknownHandling { get; }

    /// <summary>Gets whether this unknown state requires coverage by a pending application.</summary>
    public bool RequiresApplication =>
        Kind == SettingAppliedStateKind.Unknown
        && UnknownHandling == SettingAppliedUnknownHandling.QueueApplication;

    /// <summary>Creates verified effective-state evidence.</summary>
    /// <param name="value">Registry-normalized effective value.</param>
    /// <param name="source">Evidence source.</param>
    /// <param name="observedHash">Canonical lowercase SHA-256 value hash.</param>
    /// <param name="observedAt">UTC observation timestamp.</param>
    /// <returns>An immutable verified applied state.</returns>
    public static SettingAppliedState Verified(
        SettingValue value,
        SettingAppliedValueSource source,
        string observedHash,
        DateTimeOffset observedAt)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(observedHash);

        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source));
        }

        if (!SettingsValueHash.IsCanonicalSha256(observedHash))
        {
            throw new ArgumentException(
                "Observed hash must be a canonical lowercase SHA-256 value.",
                nameof(observedHash));
        }

        if (observedAt == default)
        {
            throw new ArgumentOutOfRangeException(
                nameof(observedAt),
                "Observation timestamp must be present.");
        }

        if (observedAt.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Observation timestamp must use UTC.", nameof(observedAt));
        }

        return new SettingAppliedState(
            SettingAppliedStateKind.Verified,
            value,
            source,
            observedHash,
            observedAt,
            unknownReason: null,
            unknownHandling: null);
    }

    /// <summary>Creates an explicit unknown effective state.</summary>
    /// <param name="reason">Stable reason the effective value is unavailable.</param>
    /// <param name="handling">Safe behavior while the value remains unknown.</param>
    /// <returns>An immutable unknown applied state.</returns>
    public static SettingAppliedState Unknown(
        SettingAppliedUnknownReason reason,
        SettingAppliedUnknownHandling handling)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (!Enum.IsDefined(handling))
        {
            throw new ArgumentOutOfRangeException(nameof(handling));
        }

        if ((reason == SettingAppliedUnknownReason.BlockedProbe)
            == (handling == SettingAppliedUnknownHandling.QueueApplication))
        {
            throw new ArgumentException(
                "Only a blocked probe may omit automatic application coverage.",
                nameof(handling));
        }

        return new SettingAppliedState(
            SettingAppliedStateKind.Unknown,
            value: null,
            source: null,
            observedHash: null,
            observedAt: null,
            reason,
            handling);
    }

    /// <inheritdoc />
    public bool Equals(SettingAppliedState? other) =>
        other is not null
        && Kind == other.Kind
        && Equals(Value, other.Value)
        && Source == other.Source
        && StringComparer.Ordinal.Equals(ObservedHash, other.ObservedHash)
        && ObservedAt == other.ObservedAt
        && UnknownReason == other.UnknownReason
        && UnknownHandling == other.UnknownHandling;

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SettingAppliedState);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.Add(Kind);
        hash.Add(Value);
        hash.Add(Source);
        hash.Add(ObservedHash, StringComparer.Ordinal);
        hash.Add(ObservedAt);
        hash.Add(UnknownReason);
        hash.Add(UnknownHandling);
        return hash.ToHashCode();
    }
}
