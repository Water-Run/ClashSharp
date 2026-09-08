using System.Collections.ObjectModel;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Captures the complete immutable configuration actually installed for internal consumers in one generation.</summary>
public sealed class InternalSettingsSnapshot
{
    internal InternalSettingsSnapshot(DataGenerationDescriptor generation, IEnumerable<KeyValuePair<SettingKey, SettingValue>> values)
    {
        Generation = generation;
        Values = new ReadOnlyDictionary<SettingKey, SettingValue>(values.ToDictionary());
    }

    /// <summary>Gets the exact lifetime that published this historical snapshot.</summary>
    public DataGenerationDescriptor Generation { get; }

    /// <summary>Gets installed values only, excluding pending desired intent and other participants' settings.</summary>
    public IReadOnlyDictionary<SettingKey, SettingValue> Values { get; }

    /// <summary>Reads an exact typed internal value without storage I/O.</summary>
    /// <typeparam name="T">The registry-declared immutable value type.</typeparam>
    /// <param name="key">An internal consumer key.</param>
    /// <returns>The value installed when this snapshot was captured.</returns>
    public T Get<T>(SettingKey key) where T : notnull => Values[key].Get<T>();
}
