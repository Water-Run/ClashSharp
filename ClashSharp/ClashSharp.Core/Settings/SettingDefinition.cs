using System.Globalization;

namespace ClashSharp.Settings;

/// <summary>Contains immutable non-value metadata shared by one setting definition.</summary>
public sealed class SettingDefinitionMetadata
{
    private const SettingsResetScope DeclaredResetScopes =
        SettingsResetScope.Basic
        | SettingsResetScope.Notifications
        | SettingsResetScope.Startup
        | SettingsResetScope.Triggers
        | SettingsResetScope.Tray
        | SettingsResetScope.TransparentProxy
        | SettingsResetScope.Proxy
        | SettingsResetScope.ConnectionTests
        | SettingsResetScope.WindowsNative
        | SettingsResetScope.MainlandChina
        | SettingsResetScope.MasterControl;

    /// <summary>Initializes validated immutable definition metadata.</summary>
    /// <param name="schemaVersion">Positive schema version that introduced the canonical definition.</param>
    /// <param name="category">Stable user-facing category.</param>
    /// <param name="resetScopes">Group-specific reset memberships; <see cref="SettingsResetScope.All"/> is implicit.</param>
    /// <param name="includeInDataPackage">Whether current package exports include this setting.</param>
    /// <param name="authority">Effective-value authority classification.</param>
    /// <param name="applicationKind">Participant responsible for applying the setting.</param>
    /// <param name="applicationTiming">Whether the value is reconciled live or at restart.</param>
    /// <param name="localizationCategory">Stable localization resource prefix.</param>
    /// <param name="isSensitive">Whether diagnostics must redact the canonical value.</param>
    /// <param name="aliases">Read-only legacy persisted keys.</param>
    public SettingDefinitionMetadata(
        int schemaVersion,
        SettingCategory category,
        SettingsResetScope resetScopes,
        bool includeInDataPackage,
        SettingAuthority authority,
        SettingApplicationKind applicationKind,
        SettingApplicationTiming applicationTiming,
        string localizationCategory,
        bool isSensitive,
        IEnumerable<SettingKey>? aliases = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localizationCategory);

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Schema version must be positive.");
        }

        if (!Enum.IsDefined(category))
        {
            throw new ArgumentOutOfRangeException(nameof(category));
        }

        if (!Enum.IsDefined(authority))
        {
            throw new ArgumentOutOfRangeException(nameof(authority));
        }

        if (!Enum.IsDefined(applicationKind))
        {
            throw new ArgumentOutOfRangeException(nameof(applicationKind));
        }

        if (!Enum.IsDefined(applicationTiming))
        {
            throw new ArgumentOutOfRangeException(nameof(applicationTiming));
        }

        if ((authority == SettingAuthority.RestartBound)
            != (applicationTiming == SettingApplicationTiming.Restart))
        {
            throw new ArgumentException(
                "Restart-bound authority and restart application timing must be declared together.",
                nameof(applicationTiming));
        }

        if ((resetScopes & ~DeclaredResetScopes) != SettingsResetScope.None)
        {
            throw new ArgumentOutOfRangeException(nameof(resetScopes), "Reset scopes contain an undefined or aggregate flag.");
        }

        SettingKey[] aliasSnapshot = aliases?.ToArray() ?? [];
        if (aliasSnapshot.Any(static alias => alias is null)
            || aliasSnapshot.Distinct().Count() != aliasSnapshot.Length)
        {
            throw new ArgumentException("Setting aliases must be non-null and unique.", nameof(aliases));
        }

        SchemaVersion = schemaVersion;
        Category = category;
        ResetScopes = resetScopes;
        IncludeInDataPackage = includeInDataPackage;
        Authority = authority;
        ApplicationKind = applicationKind;
        ApplicationTiming = applicationTiming;
        LocalizationCategory = localizationCategory;
        IsSensitive = isSensitive;
        Aliases = Array.AsReadOnly(aliasSnapshot);
    }

    /// <summary>Gets the schema version that introduced the canonical definition.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the stable user-facing category.</summary>
    public SettingCategory Category { get; }

    /// <summary>Gets the group-specific reset memberships.</summary>
    public SettingsResetScope ResetScopes { get; }

    /// <summary>Gets whether current package exports include the setting.</summary>
    public bool IncludeInDataPackage { get; }

    /// <summary>Gets the effective-value authority classification.</summary>
    public SettingAuthority Authority { get; }

    /// <summary>Gets the participant responsible for applying the setting.</summary>
    public SettingApplicationKind ApplicationKind { get; }

    /// <summary>Gets whether the setting is reconciled live or at restart.</summary>
    public SettingApplicationTiming ApplicationTiming { get; }

    /// <summary>Gets the stable localization resource prefix.</summary>
    public string LocalizationCategory { get; }

    /// <summary>Gets whether diagnostics must redact this setting's value.</summary>
    public bool IsSensitive { get; }

    /// <summary>Gets read-only legacy persisted keys.</summary>
    public IReadOnlyList<SettingKey> Aliases { get; }
}

/// <summary>
/// Describes one setting's canonical type, values, validation, package, reset, authority, and application metadata.
/// </summary>
public sealed class SettingDefinition
{
    private readonly Func<string, SettingNormalizationResult> _textNormalizer;
    private readonly Func<object, SettingNormalizationResult> _typedNormalizer;

    private SettingDefinition(
        SettingKey key,
        SettingDefinitionMetadata metadata,
        Type valueType,
        SettingValue defaultValue,
        SettingValue safeFallback,
        IReadOnlyList<SettingValue> allowedValues,
        Func<string, SettingNormalizationResult> textNormalizer,
        Func<object, SettingNormalizationResult> typedNormalizer)
    {
        if (metadata.Aliases.Contains(key))
        {
            throw new ArgumentException("A canonical key cannot also be its own alias.", nameof(metadata));
        }

        Key = key;
        SchemaVersion = metadata.SchemaVersion;
        Category = metadata.Category;
        ResetScopes = metadata.ResetScopes;
        IncludeInDataPackage = metadata.IncludeInDataPackage;
        Authority = metadata.Authority;
        ApplicationKind = metadata.ApplicationKind;
        ApplicationTiming = metadata.ApplicationTiming;
        LocalizationCategory = metadata.LocalizationCategory;
        IsSensitive = metadata.IsSensitive;
        Aliases = metadata.Aliases;
        ValueType = valueType;
        DefaultValue = defaultValue;
        SafeFallback = safeFallback;
        AllowedValues = allowedValues;
        _textNormalizer = textNormalizer;
        _typedNormalizer = typedNormalizer;
    }

    /// <summary>Gets the canonical persisted key.</summary>
    public SettingKey Key { get; }

    /// <summary>Gets the schema version that introduced this definition.</summary>
    public int SchemaVersion { get; }

    /// <summary>Gets the exact CLR value type.</summary>
    public Type ValueType { get; }

    /// <summary>Gets the canonical default value.</summary>
    public SettingValue DefaultValue { get; }

    /// <summary>Gets the safe value used when the effective state is unknown.</summary>
    public SettingValue SafeFallback { get; }

    /// <summary>Gets finite canonical choices, or an empty collection when the value domain is not finite.</summary>
    public IReadOnlyList<SettingValue> AllowedValues { get; }

    /// <summary>Gets read-only legacy persisted keys.</summary>
    public IReadOnlyList<SettingKey> Aliases { get; }

    /// <summary>Gets the stable user-facing category.</summary>
    public SettingCategory Category { get; }

    /// <summary>Gets the group-specific reset memberships.</summary>
    public SettingsResetScope ResetScopes { get; }

    /// <summary>Gets whether current package exports include this setting.</summary>
    public bool IncludeInDataPackage { get; }

    /// <summary>Gets the effective-value authority classification.</summary>
    public SettingAuthority Authority { get; }

    /// <summary>Gets the participant responsible for applying this setting.</summary>
    public SettingApplicationKind ApplicationKind { get; }

    /// <summary>Gets whether the setting is reconciled live or at restart.</summary>
    public SettingApplicationTiming ApplicationTiming { get; }

    /// <summary>Gets the stable localization resource prefix.</summary>
    public string LocalizationCategory { get; }

    /// <summary>Gets whether diagnostics must redact this setting's value.</summary>
    public bool IsSensitive { get; }

    /// <summary>Normalizes textual user or persistence input without throwing for expected invalid values.</summary>
    /// <param name="input">Text to parse and validate.</param>
    /// <returns>A canonical typed value or a stable expected failure.</returns>
    public SettingNormalizationResult Normalize(string? input) =>
        input is null
            ? SettingNormalizationResult.Failed(Error(SettingValueErrorKind.Missing, "missing"))
            : _textNormalizer(input);

    /// <summary>Normalizes an exact typed value without accepting implicit numeric or enum conversions.</summary>
    /// <param name="value">Typed value to validate.</param>
    /// <returns>A canonical typed value or a stable expected failure.</returns>
    public SettingNormalizationResult NormalizeValue(object? value) =>
        value is null
            ? SettingNormalizationResult.Failed(Error(SettingValueErrorKind.Missing, "missing"))
            : _typedNormalizer(value);

    /// <summary>Returns whether this definition participates in a registry-derived reset.</summary>
    /// <param name="scope">Requested reset scope.</param>
    /// <returns>True when this setting must reset for the requested scope.</returns>
    public bool IsInResetScope(SettingsResetScope scope) =>
        scope == SettingsResetScope.All
        || scope != SettingsResetScope.None && (ResetScopes & scope) != SettingsResetScope.None;

    /// <summary>Creates a strict canonical boolean definition.</summary>
    /// <param name="key">Canonical persisted key.</param>
    /// <param name="defaultValue">Canonical default value.</param>
    /// <param name="safeFallback">Value used when effective state is unknown.</param>
    /// <param name="metadata">Immutable non-value metadata.</param>
    /// <returns>A validated boolean definition.</returns>
    public static SettingDefinition CreateBoolean(
        SettingKey key,
        bool defaultValue,
        bool safeFallback,
        SettingDefinitionMetadata metadata)
    {
        return Create(
            key,
            defaultValue,
            safeFallback,
            static input => input switch
            {
                "true" => ParseOutcome<bool>.Succeeded(true),
                "false" => ParseOutcome<bool>.Succeeded(false),
                _ => ParseOutcome<bool>.Failed(Error(SettingValueErrorKind.InvalidFormat, "boolean.invalid_format")),
            },
            static value => value ? "true" : "false",
            metadata,
            [false, true]);
    }

    /// <summary>Creates a strict canonical 32-bit integer definition.</summary>
    /// <param name="key">Canonical persisted key.</param>
    /// <param name="defaultValue">Canonical default value.</param>
    /// <param name="safeFallback">Value used when effective state is unknown.</param>
    /// <param name="minimum">Inclusive minimum value.</param>
    /// <param name="maximum">Inclusive maximum value.</param>
    /// <param name="metadata">Immutable non-value metadata.</param>
    /// <returns>A validated integer definition.</returns>
    public static SettingDefinition CreateInteger(
        SettingKey key,
        int defaultValue,
        int safeFallback,
        int minimum,
        int maximum,
        SettingDefinitionMetadata metadata)
    {
        if (minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "Minimum cannot exceed maximum.");
        }

        return Create(
            key,
            defaultValue,
            safeFallback,
            input => ParseInteger(input, minimum, maximum),
            static value => value.ToString(CultureInfo.InvariantCulture),
            metadata,
            allowedValues: null);
    }

    /// <summary>Creates a strict named enum definition that never accepts numeric enum text.</summary>
    /// <typeparam name="TEnum">Declared enum type.</typeparam>
    /// <param name="key">Canonical persisted key.</param>
    /// <param name="defaultValue">Canonical default value.</param>
    /// <param name="safeFallback">Value used when effective state is unknown.</param>
    /// <param name="metadata">Immutable non-value metadata.</param>
    /// <param name="allowedValues">Optional ordered subset of enum members valid for this setting.</param>
    /// <returns>A validated enum definition.</returns>
    public static SettingDefinition CreateEnum<TEnum>(
        SettingKey key,
        TEnum defaultValue,
        TEnum safeFallback,
        SettingDefinitionMetadata metadata,
        IEnumerable<TEnum>? allowedValues = null)
        where TEnum : struct, Enum
    {
        TEnum[] options = allowedValues?.ToArray()
            ?? Enum.GetValues<TEnum>()
                .OrderBy(static value => Convert.ToInt64(value, CultureInfo.InvariantCulture))
                .ToArray();
        HashSet<TEnum> allowedSet = [.. options];

        return Create(
            key,
            defaultValue,
            safeFallback,
            input => ParseEnum(input, allowedSet),
            static value => value.ToString(),
            metadata,
            options);
    }

    /// <summary>Creates an exact string definition for synthetic or unconstrained values.</summary>
    /// <param name="key">Canonical persisted key.</param>
    /// <param name="defaultValue">Canonical default value.</param>
    /// <param name="safeFallback">Value used when effective state is unknown.</param>
    /// <param name="metadata">Immutable non-value metadata.</param>
    /// <returns>A validated string definition.</returns>
    public static SettingDefinition CreateString(
        SettingKey key,
        string defaultValue,
        string safeFallback,
        SettingDefinitionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(defaultValue);
        ArgumentNullException.ThrowIfNull(safeFallback);

        return CreateString(
            key,
            defaultValue,
            safeFallback,
            static input => StringNormalizationOutcome.Succeeded(input),
            metadata);
    }

    internal static SettingDefinition CreateString(
        SettingKey key,
        string defaultValue,
        string safeFallback,
        SettingStringNormalizer normalizer,
        SettingDefinitionMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(normalizer);

        return Create(
            key,
            defaultValue,
            safeFallback,
            input =>
            {
                StringNormalizationOutcome result = normalizer(input);
                return result.IsSuccess
                    ? ParseOutcome<string>.Succeeded(result.Value!)
                    : ParseOutcome<string>.Failed(result.Error!);
            },
            static value => value,
            metadata,
            allowedValues: null);
    }

    internal static SettingValueError Error(SettingValueErrorKind kind, string suffix) =>
        new(kind, $"settings.value.{suffix}");

    private static SettingDefinition Create<T>(
        SettingKey key,
        T defaultValue,
        T safeFallback,
        Func<string, ParseOutcome<T>> parser,
        Func<T, string> formatter,
        SettingDefinitionMetadata metadata,
        IEnumerable<T>? allowedValues)
        where T : notnull
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(defaultValue);
        ArgumentNullException.ThrowIfNull(safeFallback);
        ArgumentNullException.ThrowIfNull(parser);
        ArgumentNullException.ThrowIfNull(formatter);
        ArgumentNullException.ThrowIfNull(metadata);

        SettingNormalizationResult NormalizeText(string input)
        {
            ParseOutcome<T> parsed = parser(input);
            if (!parsed.IsSuccess)
            {
                return SettingNormalizationResult.Failed(parsed.Error!);
            }

            T value = parsed.Value!;
            string canonicalText = formatter(value);
            return SettingNormalizationResult.Succeeded(new SettingValue(typeof(T), value, canonicalText));
        }

        SettingNormalizationResult NormalizeTyped(object value)
        {
            if (value is not T typedValue)
            {
                return SettingNormalizationResult.Failed(Error(SettingValueErrorKind.InvalidType, "invalid_type"));
            }

            return NormalizeText(formatter(typedValue));
        }

        SettingNormalizationResult normalizedDefault = NormalizeTyped(defaultValue);
        if (!normalizedDefault.IsSuccess)
        {
            throw new ArgumentException(
                $"Default value for setting '{key.Value}' is invalid: {normalizedDefault.Error!.Code}.",
                nameof(defaultValue));
        }

        SettingNormalizationResult normalizedFallback = NormalizeTyped(safeFallback);
        if (!normalizedFallback.IsSuccess)
        {
            throw new ArgumentException(
                $"Safe fallback for setting '{key.Value}' is invalid: {normalizedFallback.Error!.Code}.",
                nameof(safeFallback));
        }

        List<SettingValue> normalizedAllowedValues = [];
        if (allowedValues is not null)
        {
            foreach (T allowedValue in allowedValues)
            {
                SettingNormalizationResult normalizedAllowed = NormalizeTyped(allowedValue);
                if (!normalizedAllowed.IsSuccess)
                {
                    throw new ArgumentException(
                        $"Allowed value for setting '{key.Value}' is invalid: {normalizedAllowed.Error!.Code}.",
                        nameof(allowedValues));
                }

                if (normalizedAllowedValues.Contains(normalizedAllowed.Value!))
                {
                    throw new ArgumentException(
                        $"Allowed values for setting '{key.Value}' contain a duplicate.",
                        nameof(allowedValues));
                }

                normalizedAllowedValues.Add(normalizedAllowed.Value!);
            }
        }

        return new SettingDefinition(
            key,
            metadata,
            typeof(T),
            normalizedDefault.Value!,
            normalizedFallback.Value!,
            normalizedAllowedValues.AsReadOnly(),
            NormalizeText,
            NormalizeTyped);
    }

    private static ParseOutcome<int> ParseInteger(string input, int minimum, int maximum)
    {
        if (!int.TryParse(input, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int value)
            || !StringComparer.Ordinal.Equals(input, value.ToString(CultureInfo.InvariantCulture)))
        {
            return ParseOutcome<int>.Failed(Error(SettingValueErrorKind.InvalidFormat, "integer.invalid_format"));
        }

        return value < minimum || value > maximum
            ? ParseOutcome<int>.Failed(Error(SettingValueErrorKind.OutOfRange, "integer.out_of_range"))
            : ParseOutcome<int>.Succeeded(value);
    }

    private static ParseOutcome<TEnum> ParseEnum<TEnum>(
        string input,
        IReadOnlySet<TEnum> allowedValues)
        where TEnum : struct, Enum
    {
        if (!Enum.TryParse(input, ignoreCase: false, out TEnum value)
            || !Enum.IsDefined(value)
            || !StringComparer.Ordinal.Equals(input, Enum.GetName(value))
            || !allowedValues.Contains(value))
        {
            return ParseOutcome<TEnum>.Failed(Error(SettingValueErrorKind.UndefinedValue, "enum.undefined"));
        }

        return ParseOutcome<TEnum>.Succeeded(value);
    }

    private sealed class ParseOutcome<T>
        where T : notnull
    {
        private ParseOutcome(bool isSuccess, T? value, SettingValueError? error)
        {
            IsSuccess = isSuccess;
            Value = value;
            Error = error;
        }

        public bool IsSuccess { get; }

        public T? Value { get; }

        public SettingValueError? Error { get; }

        public static ParseOutcome<T> Succeeded(T value) => new(true, value, null);

        public static ParseOutcome<T> Failed(SettingValueError error) => new(false, default, error);
    }
}

internal delegate StringNormalizationOutcome SettingStringNormalizer(string input);

internal readonly record struct StringNormalizationOutcome(
    bool IsSuccess,
    string? Value,
    SettingValueError? Error)
{
    public static StringNormalizationOutcome Succeeded(string value) => new(true, value, null);

    public static StringNormalizationOutcome Failed(SettingValueError error) => new(false, null, error);
}
