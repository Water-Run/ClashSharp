using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Owns one immutable, allowlisted legacy preference snapshot without internal credentials.</summary>
public sealed class LegacySettingsSnapshot
{
    private const int MaximumStringLength = 1024 * 1024;
    private readonly IReadOnlyDictionary<string, object?> _values;

    /// <summary>Copies only registered preference keys and read-only aliases before computing their identity.</summary>
    /// <param name="registry">Canonical preference schema; unregistered keys are never copied or hashed.</param>
    /// <param name="values">Atomic legacy read whose primitive values are snapshotted by this constructor.</param>
    public LegacySettingsSnapshot(SettingsRegistry registry, IReadOnlyDictionary<string, object?> values)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(values);
        Dictionary<string, object?> snapshot = new(StringComparer.Ordinal);
        foreach (string key in registry.Definitions.SelectMany(
                     definition => new[] { definition.Key.Value }.Concat(definition.Aliases.Select(alias => alias.Value))))
        {
            if (!values.Keys.Contains(key, StringComparer.Ordinal) || !values.TryGetValue(key, out object? value))
            {
                continue;
            }

            if (value is string text && text.Length > MaximumStringLength)
            {
                throw new ArgumentException("A legacy preference exceeds the snapshot size limit.", nameof(values));
            }

            // LocalSettings preferences use only these immutable primitives. Unknown
            // types become an invalid-value marker without invoking arbitrary ToString.
            snapshot.Add(key, value is bool or int or string ? value : null);
        }

        _values = new ReadOnlyDictionary<string, object?>(snapshot);
        SourceHash = ComputeHash(snapshot);
    }

    /// <summary>Gets the SHA-256 identity of the allowlisted primitive snapshot.</summary>
    public string SourceHash { get; }

    /// <summary>Gets a copied raw preference; null denotes a present but unsupported legacy value.</summary>
    /// <param name="key">Canonical or registered legacy key.</param>
    /// <param name="value">Copied immutable primitive or the invalid-value marker.</param>
    /// <returns>Whether the allowlisted source contained this exact key.</returns>
    public bool TryGetValue(string key, out object? value) => _values.TryGetValue(key, out value);

    private static string ComputeHash(IReadOnlyDictionary<string, object?> values)
    {
        using MemoryStream bytes = new();
        using (Utf8JsonWriter writer = new(bytes))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "clashsharp-legacy-preferences-v1");
            writer.WriteStartObject("values");
            foreach ((string key, object? value) in values.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                switch (value)
                {
                    case bool boolean: writer.WriteBoolean(key, boolean); break;
                    case int integer: writer.WriteNumber(key, integer); break;
                    case string text: writer.WriteString(key, text); break;
                    default: writer.WriteNull(key); break;
                }
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Convert.ToHexStringLower(SHA256.HashData(bytes.GetBuffer().AsSpan(0, checked((int)bytes.Length))));
    }
}
