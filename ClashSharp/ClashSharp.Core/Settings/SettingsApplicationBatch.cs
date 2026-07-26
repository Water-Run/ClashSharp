using System.Security.Cryptography;
using System.Text;

namespace ClashSharp.Settings;

/// <summary>Classifies whether a pending application runs live or across process restart.</summary>
public enum SettingsApplicationBatchKind
{
    /// <summary>Apply and verify in the current process.</summary>
    LiveReconcile = 0,

    /// <summary>Apply and verify across a process restart boundary.</summary>
    Restart = 1,
}

/// <summary>Classifies the durable state of one pending application attempt.</summary>
public enum SettingsApplicationBatchState
{
    /// <summary>The attempt has not crossed its first side effect.</summary>
    Pending = 0,

    /// <summary>The attempt may be side-effecting and cannot be split by edits.</summary>
    Running = 1,

    /// <summary>The attempt reached a recoverable typed failure.</summary>
    Failed = 2,
}

/// <summary>Contains the stable nonlocalized failure code retained by a failed batch.</summary>
public sealed record SettingsApplicationError
{
    /// <summary>Initializes a typed application failure.</summary>
    /// <param name="code">Stable nonlocalized diagnostic code.</param>
    public SettingsApplicationError(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
    }

    /// <summary>Gets the stable nonlocalized diagnostic code.</summary>
    public string Code { get; }
}

/// <summary>Identifies one exact desired key revision and value inside a pending batch.</summary>
public sealed record SettingsApplicationBatchEntry
{
    /// <summary>Initializes an immutable pending batch entry.</summary>
    /// <param name="key">Canonical setting key.</param>
    /// <param name="keyDesiredRevision">Positive desired revision captured by the batch.</param>
    /// <param name="valueHash">Canonical lowercase SHA-256 hash of desired canonical text.</param>
    public SettingsApplicationBatchEntry(
        SettingKey key,
        long keyDesiredRevision,
        string valueHash)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyDesiredRevision);
        ArgumentException.ThrowIfNullOrWhiteSpace(valueHash);

        if (!SettingsValueHash.IsCanonicalSha256(valueHash))
        {
            throw new ArgumentException(
                "Value hash must be a canonical lowercase SHA-256 value.",
                nameof(valueHash));
        }

        Key = key;
        KeyDesiredRevision = keyDesiredRevision;
        ValueHash = valueHash;
    }

    /// <summary>Gets the canonical setting key.</summary>
    public SettingKey Key { get; }

    /// <summary>Gets the desired key revision captured by this entry.</summary>
    public long KeyDesiredRevision { get; }

    /// <summary>Gets the canonical lowercase SHA-256 desired value hash.</summary>
    public string ValueHash { get; }

    /// <summary>Creates an entry from a canonical desired setting entry.</summary>
    /// <param name="key">Canonical setting key.</param>
    /// <param name="desired">Desired value and revision to capture.</param>
    /// <returns>An immutable hash-bound batch entry.</returns>
    public static SettingsApplicationBatchEntry Create(
        SettingKey key,
        SettingDesiredEntry desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        return new SettingsApplicationBatchEntry(
            key,
            desired.KeyDesiredRevision,
            ComputeValueHash(desired.Value));
    }

    /// <summary>Computes the durable canonical lowercase SHA-256 hash for a setting value.</summary>
    /// <param name="value">Canonical setting value.</param>
    /// <returns>Lowercase 64-character SHA-256 text.</returns>
    public static string ComputeValueHash(SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return SettingsValueHash.Compute(value.CanonicalText);
    }
}

/// <summary>
/// Contains one ordered immutable application attempt with stable identity and disjoint key entries.
/// </summary>
public sealed class SettingsApplicationBatch
{
    /// <summary>Initializes a validated immutable application batch.</summary>
    /// <param name="batchId">Stable nonempty batch identity.</param>
    /// <param name="kind">Live or restart application ordering class.</param>
    /// <param name="creationSequence">Positive monotonic creation sequence.</param>
    /// <param name="attemptId">Stable nonempty current attempt identity.</param>
    /// <param name="state">Current durable attempt state.</param>
    /// <param name="applicationKind">Participant responsible for all entries.</param>
    /// <param name="entries">Nonempty unique entries, stored in canonical key order.</param>
    /// <param name="lastError">Required only for a failed batch.</param>
    public SettingsApplicationBatch(
        Guid batchId,
        SettingsApplicationBatchKind kind,
        long creationSequence,
        Guid attemptId,
        SettingsApplicationBatchState state,
        SettingApplicationKind applicationKind,
        IEnumerable<SettingsApplicationBatchEntry> entries,
        SettingsApplicationError? lastError = null)
    {
        if (batchId == Guid.Empty)
        {
            throw new ArgumentException("Batch identity cannot be empty.", nameof(batchId));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(creationSequence);

        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Attempt identity cannot be empty.", nameof(attemptId));
        }

        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state));
        }

        if (!Enum.IsDefined(applicationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(applicationKind));
        }

        ArgumentNullException.ThrowIfNull(entries);
        SettingsApplicationBatchEntry[] snapshot = entries.ToArray();
        if (snapshot.Length == 0
            || snapshot.Any(static entry => entry is null)
            || snapshot.Select(static entry => entry.Key).Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Batch entries must be nonempty, non-null, and unique by key.",
                nameof(entries));
        }

        if ((state == SettingsApplicationBatchState.Failed) != (lastError is not null))
        {
            throw new ArgumentException(
                "Only failed batches must retain a last error.",
                nameof(lastError));
        }

        Array.Sort(
            snapshot,
            static (left, right) =>
                StringComparer.Ordinal.Compare(left.Key.Value, right.Key.Value));

        BatchId = batchId;
        Kind = kind;
        CreationSequence = creationSequence;
        AttemptId = attemptId;
        State = state;
        ApplicationKind = applicationKind;
        Entries = Array.AsReadOnly(snapshot);
        LastError = lastError;
    }

    /// <summary>Gets the stable batch identity.</summary>
    public Guid BatchId { get; }

    /// <summary>Gets the live/restart ordering class.</summary>
    public SettingsApplicationBatchKind Kind { get; }

    /// <summary>Gets the monotonic batch creation sequence.</summary>
    public long CreationSequence { get; }

    /// <summary>Gets the stable current attempt identity.</summary>
    public Guid AttemptId { get; }

    /// <summary>Gets the current attempt state.</summary>
    public SettingsApplicationBatchState State { get; }

    /// <summary>Gets the participant responsible for every entry.</summary>
    public SettingApplicationKind ApplicationKind { get; }

    /// <summary>Gets entries in canonical key order.</summary>
    public IReadOnlyList<SettingsApplicationBatchEntry> Entries { get; }

    /// <summary>Gets the retained typed failure for a failed batch.</summary>
    public SettingsApplicationError? LastError { get; }

    internal SettingsApplicationBatch WithEntries(
        IEnumerable<SettingsApplicationBatchEntry> entries) =>
        new(
            BatchId,
            Kind,
            CreationSequence,
            AttemptId,
            State,
            ApplicationKind,
            entries,
            LastError);
}

internal sealed class SettingsApplicationBatchComparer : IComparer<SettingsApplicationBatch>
{
    public static SettingsApplicationBatchComparer Instance { get; } = new();

    public int Compare(SettingsApplicationBatch? x, SettingsApplicationBatch? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return -1;
        }

        if (y is null)
        {
            return 1;
        }

        int comparison = x.Kind.CompareTo(y.Kind);
        if (comparison != 0)
        {
            return comparison;
        }

        comparison = x.CreationSequence.CompareTo(y.CreationSequence);
        return comparison != 0 ? comparison : x.BatchId.CompareTo(y.BatchId);
    }
}

internal static class SettingsValueHash
{
    public static string Compute(string canonicalText)
    {
        ArgumentNullException.ThrowIfNull(canonicalText);
        byte[] bytes = Encoding.UTF8.GetBytes(canonicalText);
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static bool IsCanonicalSha256(string value) =>
        value.Length == 64
        && value.All(static character =>
            character is >= '0' and <= '9'
            || character is >= 'a' and <= 'f');
}
