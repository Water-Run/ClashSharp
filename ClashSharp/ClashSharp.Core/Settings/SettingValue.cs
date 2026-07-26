namespace ClashSharp.Settings;

/// <summary>Classifies why a setting value could not be normalized.</summary>
public enum SettingValueErrorKind
{
    /// <summary>The source value was absent.</summary>
    Missing = 0,

    /// <summary>The typed source value had the wrong CLR type.</summary>
    InvalidType = 1,

    /// <summary>The textual representation was malformed or noncanonical.</summary>
    InvalidFormat = 2,

    /// <summary>The value was outside its declared numeric range.</summary>
    OutOfRange = 3,

    /// <summary>The value did not identify a declared enum member or allowed identifier.</summary>
    UndefinedValue = 4,

    /// <summary>The value contained unsafe identity, path-like, or credential data.</summary>
    UnsafeValue = 5,
}

/// <summary>Describes one stable setting-normalization failure.</summary>
/// <param name="Kind">Machine-readable failure classification.</param>
/// <param name="Code">Stable nonlocalized diagnostic code.</param>
public sealed record SettingValueError(SettingValueErrorKind Kind, string Code);

/// <summary>Contains an immutable typed setting value and its invariant durable representation.</summary>
public sealed class SettingValue : IEquatable<SettingValue>
{
    private readonly object _value;

    internal SettingValue(Type valueType, object value, string canonicalText)
    {
        ValueType = valueType;
        _value = value;
        CanonicalText = canonicalText;
    }

    /// <summary>Gets the exact CLR value type declared by the setting definition.</summary>
    public Type ValueType { get; }

    /// <summary>Gets the invariant canonical text used for persistence and hashing.</summary>
    public string CanonicalText { get; }

    /// <summary>Returns the value when its declared type is exactly <typeparamref name="T"/>.</summary>
    /// <typeparam name="T">Expected setting value type.</typeparam>
    /// <returns>The immutable typed value.</returns>
    /// <exception cref="InvalidOperationException">The requested type is not the declared value type.</exception>
    public T Get<T>()
        where T : notnull
    {
        if (ValueType != typeof(T))
        {
            throw new InvalidOperationException(
                $"Setting value type is '{ValueType.FullName}', not '{typeof(T).FullName}'.");
        }

        return (T)_value;
    }

    /// <inheritdoc />
    public bool Equals(SettingValue? other) =>
        other is not null
        && ValueType == other.ValueType
        && StringComparer.Ordinal.Equals(CanonicalText, other.CanonicalText);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SettingValue);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(ValueType, StringComparer.Ordinal.GetHashCode(CanonicalText));

    /// <inheritdoc />
    public override string ToString() => CanonicalText;
}

/// <summary>Represents the success or expected failure of setting-value normalization.</summary>
public sealed class SettingNormalizationResult
{
    private SettingNormalizationResult(SettingValue? value, SettingValueError? error)
    {
        Value = value;
        Error = error;
    }

    /// <summary>Gets whether normalization produced a valid canonical value.</summary>
    public bool IsSuccess => Value is not null;

    /// <summary>Gets the normalized value, or null when normalization failed.</summary>
    public SettingValue? Value { get; }

    /// <summary>Gets the stable failure, or null when normalization succeeded.</summary>
    public SettingValueError? Error { get; }

    internal static SettingNormalizationResult Succeeded(SettingValue value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SettingNormalizationResult(value, null);
    }

    internal static SettingNormalizationResult Failed(SettingValueError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new SettingNormalizationResult(null, error);
    }
}
