using System.Globalization;
using ClashSharp.Settings;
using Windows.Management.Core;
using Windows.Storage;
using Windows.System.UserProfile;

namespace ClashSharp.Platform.Regional;

/// <summary>Stages one atomic, current-user seed consumed only by the first settings migration.</summary>
internal static class WindowsRegionalDefaults
{
    internal const string SeedKey = "ClashSharp.FirstInstallDefaults.v1";

    internal static IReadOnlyDictionary<string, object> Capture()
    {
        IReadOnlyList<string> clocks = GlobalizationPreferences.Clocks;
        return RegionalDefaultsPolicy.Create(CultureInfo.CurrentUICulture.Name,
            GlobalizationPreferences.HomeGeographicRegion, TimeZoneInfo.Local.Id,
            clocks.Count > 0 ? clocks[0] : string.Empty);
    }

    /// <summary>Called after successful deployment under the installer's exact-user application lock.</summary>
    internal static void Initialize(string packageFamilyName)
    {
        ApplicationData data = ApplicationDataManager.CreateForPackageFamily(packageFamilyName);
        // Repair, upgrade and retained user data must never acquire new installation preferences.
        if (data.LocalSettings.Values.Count != 0 || Directory.Exists(Path.Combine(data.LocalFolder.Path, "Data"))) { return; }
        ApplicationDataCompositeValue seed = new();
        foreach ((string key, object value) in Capture()) { seed.Add(key, value); }
        data.LocalSettings.Values.Add(SeedKey, seed);
    }

    internal static IReadOnlyDictionary<string, object> ReadSeed(ApplicationData data)
    {
        IReadOnlyDictionary<string, object> defaults = Capture();
        if (!data.LocalSettings.Values.TryGetValue(SeedKey, out object? value) || value is not ApplicationDataCompositeValue seed)
        {
            return defaults;
        }
        Dictionary<string, object> result = new(defaults, StringComparer.Ordinal);
        foreach (string key in defaults.Keys)
        {
            if (!seed.TryGetValue(key, out object? candidate)) { continue; }
            SettingDefinition definition = SettingsRegistry.Default.Get(key);
            object? normalized = definition.ValueType.IsEnum && candidate is int number
                ? Enum.ToObject(definition.ValueType, number) : candidate;
            if (definition.NormalizeValue(normalized).IsSuccess) { result[key] = candidate!; }
        }
        return result;
    }
}
