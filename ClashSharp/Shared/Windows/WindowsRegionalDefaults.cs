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

    /// <summary>Initializes selected defaults only before any application settings generation exists.</summary>
    internal static bool InitializeSelected(ApplicationData data, IReadOnlyDictionary<string, object> selected)
    {
        if (Directory.Exists(Path.Combine(data.LocalFolder.Path, "Data"))
            || data.LocalSettings.Values.Keys.Any(key => key != SeedKey
                && key != InstallationSetupProtocol.RequestKey && key != InstallationSetupProtocol.ReceiptKey
                && key != InstallationSetupProtocol.TaskbarRequestKey && key != InstallationSetupProtocol.TaskbarReceiptKey)) { return false; }
        ApplicationDataCompositeValue seed = new();
        foreach ((string key, object value) in selected)
        {
            if (!AllowedSeedKeys.Contains(key, StringComparer.Ordinal)) { throw new ArgumentException("Unsupported initial preference.", nameof(selected)); }
            SettingDefinition definition = SettingsRegistry.Default.Get(key);
            object normalized = definition.ValueType.IsEnum && value is int number ? Enum.ToObject(definition.ValueType, number) : value;
            if (!definition.NormalizeValue(normalized).IsSuccess) { throw new ArgumentException("Invalid initial preference.", nameof(selected)); }
            if (key == SettingsRegistry.Keys.McpAllowedScopes.Value && value is not 1)
            {
                throw new ArgumentException("Installation may grant read-status access only.", nameof(selected));
            }
            seed.Add(key, value);
        }
        data.LocalSettings.Values[SeedKey] = seed;
        return true;
    }

    private static readonly string[] AllowedSeedKeys =
    [
        "DisplayLanguage", "DataUnitStyle", "RateUnit", "ClockFormat", "MainlandChinaFeatureMode", "MainlandChinaUrlBlockingEnabled",
        "LaunchAtStartupEnabled", "NotificationEnabled", "NotificationLevel", "TransparentProxyEnabled", "TrayUseMonochromeInactiveIcon",
        "McpServerEnabled", "McpAllowedScopes",
    ];

    internal static IReadOnlyDictionary<string, object> ReadSeed(ApplicationData data)
    {
        IReadOnlyDictionary<string, object> defaults = Capture();
        if (!data.LocalSettings.Values.TryGetValue(SeedKey, out object? value) || value is not ApplicationDataCompositeValue seed)
        {
            return defaults;
        }
        Dictionary<string, object> result = new(defaults, StringComparer.Ordinal);
        foreach (string key in AllowedSeedKeys)
        {
            if (!seed.TryGetValue(key, out object? candidate)) { continue; }
            if (key == SettingsRegistry.Keys.McpAllowedScopes.Value && candidate is not 1) { continue; }
            SettingDefinition definition = SettingsRegistry.Default.Get(key);
            object? normalized = definition.ValueType.IsEnum && candidate is int number
                ? Enum.ToObject(definition.ValueType, number) : candidate;
            if (definition.NormalizeValue(normalized).IsSuccess) { result[key] = candidate!; }
        }
        return result;
    }
}
