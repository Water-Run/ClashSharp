namespace ClashSharp.Settings;

/// <summary>Identifies one canonical or legacy application setting using a stable persisted name.</summary>
public sealed record SettingKey
{
    private const int MaximumLength = 128;

    /// <summary>Initializes a validated setting key.</summary>
    /// <param name="value">Stable case-sensitive persisted name.</param>
    /// <exception cref="ArgumentNullException"><paramref name="value"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="value"/> is not a safe stable identifier.</exception>
    public SettingKey(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (value.Length is 0 or > MaximumLength
            || !IsAsciiLetter(value[0])
            || value.Any(static character => !IsAllowedCharacter(character)))
        {
            throw new ArgumentException(
                "A setting key must start with an ASCII letter and contain only ASCII letters, digits, '.', '_', or '-'.",
                nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the case-sensitive persisted name.</summary>
    public string Value { get; }

    /// <inheritdoc />
    public override string ToString() => Value;

    private static bool IsAllowedCharacter(char character) =>
        IsAsciiLetter(character)
        || character is >= '0' and <= '9'
        || character is '.' or '_' or '-';

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z'
        || character is >= 'a' and <= 'z';
}
