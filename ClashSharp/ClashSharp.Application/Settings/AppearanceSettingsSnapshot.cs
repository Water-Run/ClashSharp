using System.Collections.ObjectModel;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Reads the installed tray, regional and tile configuration without exposing preference mutation or platform access.</summary>
public interface IAppearanceSettingsReader
{
    /// <summary>Captures one immutable configuration from the owning generation.</summary>
    AppearanceSettingsSnapshot CaptureSnapshot();
}

/// <summary>Contains the installed policies consumed by tray, regional display and tile layout services.</summary>
/// <remarks>Native language, theme and accent observations belong to the UI adapter and are not cached here.</remarks>
public sealed class AppearanceSettingsSnapshot
{
    internal AppearanceSettingsSnapshot(DataGenerationDescriptor generation, IEnumerable<KeyValuePair<SettingKey, SettingValue>> values)
    {
        Generation = generation;
        Values = new ReadOnlyDictionary<SettingKey, SettingValue>(values.ToDictionary());
    }

    /// <summary>Gets the lifetime that owns these installed policies.</summary>
    public DataGenerationDescriptor Generation { get; }
    /// <summary>Gets the complete installed policy set, excluding desired intent and native UI values.</summary>
    public IReadOnlyDictionary<SettingKey, SettingValue> Values { get; }

    /// <summary>Reads a registry-typed immutable policy value.</summary>
    /// <typeparam name="T">The exact registry type.</typeparam>
    /// <param name="key">A tray, regional or tile policy key.</param>
    /// <returns>The value installed when the snapshot was captured.</returns>
    public T Get<T>(SettingKey key) where T : notnull => Values[key].Get<T>();
}
