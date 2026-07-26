using System.Globalization;
using System.Text.Json;
using ClashSharp.Settings;

namespace ClashSharp.Infrastructure.Settings;

internal static partial class SettingsEnvelopeCodec
{
    private static IReadOnlyDictionary<string, JsonElement> ReadShape(
        JsonElement element,
        IReadOnlyList<string> propertyNames,
        string path)
    {
        IReadOnlyDictionary<string, JsonElement> properties =
            ReadObject(element, propertyNames, path);
        RequireShape(properties, propertyNames, path);
        return properties;
    }

    private static IReadOnlyDictionary<string, JsonElement> ReadObject(
        JsonElement element,
        IReadOnlyList<string> allowedPropertyNames,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw Error("settings.persistence.envelope.shape_invalid", path);
        }

        HashSet<string> allowed = new(
            allowedPropertyNames,
            StringComparer.Ordinal);
        Dictionary<string, JsonElement> properties =
            new(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name)
                || !properties.TryAdd(property.Name, property.Value))
            {
                throw Error(
                    "settings.persistence.envelope.shape_invalid",
                    $"{path}.{property.Name}");
            }
        }

        return properties;
    }

    private static void RequireShape(
        IReadOnlyDictionary<string, JsonElement> properties,
        IReadOnlyList<string> propertyNames,
        string path)
    {
        if (properties.Count != propertyNames.Count
            || propertyNames.Any(name => !properties.ContainsKey(name)))
        {
            throw Error("settings.persistence.envelope.shape_invalid", path);
        }
    }

    private static JsonElement.ArrayEnumerator ReadArray(
        JsonElement element,
        string path)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw Error("settings.persistence.envelope.shape_invalid", path);
        }

        return element.EnumerateArray();
    }

    private static string ReadString(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw Error("settings.persistence.envelope.shape_invalid", path);
        }

        return element.GetString()
            ?? throw Error("settings.persistence.envelope.shape_invalid", path);
    }

    private static int ReadCanonicalInt32(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt32(out int value)
            || !StringComparer.Ordinal.Equals(
                element.GetRawText(),
                value.ToString(CultureInfo.InvariantCulture)))
        {
            throw Error(
                "settings.persistence.envelope.number_noncanonical",
                path);
        }

        return value;
    }

    private static long ReadCanonicalInt64(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Number
            || !element.TryGetInt64(out long value)
            || !StringComparer.Ordinal.Equals(
                element.GetRawText(),
                value.ToString(CultureInfo.InvariantCulture)))
        {
            throw Error(
                "settings.persistence.envelope.number_noncanonical",
                path);
        }

        return value;
    }

    private static Guid ReadCanonicalGuid(JsonElement element, string path)
    {
        string text = ReadString(element, path);
        if (!Guid.TryParseExact(text, "D", out Guid value)
            || value == Guid.Empty
            || !StringComparer.Ordinal.Equals(text, value.ToString("D")))
        {
            throw Error("settings.persistence.envelope.guid_invalid", path);
        }

        return value;
    }

    private static DateTimeOffset ReadCanonicalUtcTimestamp(
        JsonElement element,
        string path)
    {
        string text = ReadString(element, path);
        if (!DateTimeOffset.TryParseExact(
                text,
                "O",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out DateTimeOffset value)
            || value == default
            || value.Offset != TimeSpan.Zero
            || !StringComparer.Ordinal.Equals(
                text,
                value.ToString("O", CultureInfo.InvariantCulture)))
        {
            throw Error(
                "settings.persistence.envelope.timestamp_invalid",
                path);
        }

        return value;
    }

    private static string ReadCanonicalHash(JsonElement element, string path)
    {
        string value = ReadString(element, path);
        if (value.Length != 64
            || value.Any(static character =>
                character is not (>= '0' and <= '9')
                    and not (>= 'a' and <= 'f')))
        {
            throw Error("settings.persistence.envelope.hash_invalid", path);
        }

        return value;
    }

    private static byte[] DecodeCanonicalBase64(string value, string path)
    {
        try
        {
            byte[] decoded = Convert.FromBase64String(value);
            if (!StringComparer.Ordinal.Equals(
                    value,
                    Convert.ToBase64String(decoded)))
            {
                throw Error(
                    "settings.persistence.envelope.base64_noncanonical",
                    path);
            }

            return decoded;
        }
        catch (FormatException exception)
        {
            throw Error(
                "settings.persistence.envelope.base64_invalid",
                path,
                exception);
        }
    }

    private static SettingDefinition ReadDefinition(
        JsonElement element,
        SettingsRegistry registry,
        string path)
    {
        string key = ReadString(element, path);
        if (!registry.TryResolve(
                key,
                out SettingDefinition? definition,
                out SettingKeyResolution resolution)
            || resolution != SettingKeyResolution.Canonical)
        {
            throw Error("settings.persistence.envelope.key_invalid", path);
        }

        return definition!;
    }

    private static SettingValue ReadValue(
        JsonElement element,
        SettingDefinition definition,
        string path)
    {
        string value = ReadString(element, path);
        SettingNormalizationResult normalized = definition.Normalize(value);
        if (!normalized.IsSuccess
            || !StringComparer.Ordinal.Equals(
                value,
                normalized.Value!.CanonicalText))
        {
            throw Error("settings.persistence.envelope.value_invalid", path);
        }

        return normalized.Value;
    }
}
