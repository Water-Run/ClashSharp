using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClashSharp.ApplicationModel.Agent;

/// <summary>Checks the closed product-authored input schemas before any admitted tool operation.</summary>
public static class McpToolArgumentValidator
{
    /// <summary>Validates types, bounds, required fields, duplicate names and unknown properties without echoing input.</summary>
    /// <param name="definition">The fixed product definition, never a caller-authored schema.</param>
    /// <param name="arguments">Untrusted detached input.</param>
    /// <returns>Whether the complete argument object satisfies the supported schema.</returns>
    public static bool IsValid(McpToolDefinition definition, JsonElement arguments)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return Validate(definition.InputSchema, arguments);
    }

    private static bool Validate(JsonElement schema, JsonElement value)
    {
        if (schema.TryGetProperty("enum", out JsonElement options) && !options.EnumerateArray().Any(option => JsonElement.DeepEquals(option, value))) { return false; }
        if (!schema.TryGetProperty("type", out JsonElement type)) { return true; }
        switch (type.GetString())
        {
            case "object":
                if (value.ValueKind != JsonValueKind.Object) { return false; }
                JsonProperty[] fields = value.EnumerateObject().ToArray();
                if (fields.Select(field => field.Name).Distinct(StringComparer.Ordinal).Count() != fields.Length
                    || !WithinBounds(schema, "minProperties", "maxProperties", fields.Length)) { return false; }
                if (schema.TryGetProperty("required", out JsonElement required)
                    && required.EnumerateArray().Any(field => !value.TryGetProperty(field.GetString()!, out _))) { return false; }
                foreach (JsonProperty field in fields)
                {
                    if (schema.TryGetProperty("properties", out JsonElement properties) && properties.TryGetProperty(field.Name, out JsonElement child))
                    {
                        if (!Validate(child, field.Value)) { return false; }
                    }
                    else if (schema.TryGetProperty("additionalProperties", out JsonElement additional) && additional.ValueKind == JsonValueKind.False) { return false; }
                }
                return true;
            case "array":
                return value.ValueKind == JsonValueKind.Array && WithinBounds(schema, "minItems", "maxItems", value.GetArrayLength())
                    && (!schema.TryGetProperty("items", out JsonElement item) || value.EnumerateArray().All(child => Validate(item, child)));
            case "integer":
                return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) && WithinBounds(schema, "minimum", "maximum", number);
            case "string":
                if (value.ValueKind != JsonValueKind.String) { return false; }
                string text = value.GetString()!;
                return WithinBounds(schema, "minLength", "maxLength", text.Length)
                    && (!schema.TryGetProperty("format", out _) || Guid.TryParse(text, out Guid id) && id != Guid.Empty)
                    && (!schema.TryGetProperty("pattern", out JsonElement pattern)
                        || Regex.IsMatch(text, pattern.GetString()!, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)));
            default: return false;
        }
    }

    private static bool WithinBounds(JsonElement schema, string minimum, string maximum, int value) =>
        (!schema.TryGetProperty(minimum, out JsonElement lower) || value >= lower.GetInt32())
        && (!schema.TryGetProperty(maximum, out JsonElement upper) || value <= upper.GetInt32());
}
