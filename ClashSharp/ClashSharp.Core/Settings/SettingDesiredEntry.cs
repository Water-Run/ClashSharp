namespace ClashSharp.Settings;

/// <summary>Contains one canonical desired value and its independently advancing key revision.</summary>
public sealed record SettingDesiredEntry
{
    /// <summary>Initializes an immutable desired setting entry.</summary>
    /// <param name="value">Registry-normalized canonical desired value.</param>
    /// <param name="keyDesiredRevision">Positive revision changed only when this key's value changes.</param>
    public SettingDesiredEntry(SettingValue value, long keyDesiredRevision)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(keyDesiredRevision);

        Value = value;
        KeyDesiredRevision = keyDesiredRevision;
    }

    /// <summary>Gets the registry-normalized canonical desired value.</summary>
    public SettingValue Value { get; }

    /// <summary>Gets the independently advancing desired revision for this key.</summary>
    public long KeyDesiredRevision { get; }
}
