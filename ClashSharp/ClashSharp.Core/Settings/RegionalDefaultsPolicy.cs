using System.Globalization;
using ClashSharp.Model;

namespace ClashSharp.Settings;

/// <summary>Selects initial preferences from the installing user's explicit region and regional format.</summary>
public static class RegionalDefaultsPolicy
{
    /// <summary>Creates legacy-compatible primitive values for a previously unused preference store.</summary>
    /// <param name="uiCultureName">Windows interface language, used only when no explicit region is available.</param>
    /// <param name="regionCode">Windows country or region; explicit non-mainland regions always take precedence.</param>
    /// <param name="timeZoneId">Windows or IANA time-zone identifier used only as supporting evidence.</param>
    /// <param name="clockIdentifier">Observed Windows clock identifier; retained without freezing its current value into an explicit preference.</param>
    public static IReadOnlyDictionary<string, object> Create(string uiCultureName, string regionCode, string timeZoneId, string clockIdentifier)
    {
        // Following Windows must remain dynamic if its clock format changes later.
        _ = clockIdentifier;
        bool mainland = string.Equals(regionCode, "CN", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(regionCode) || regionCode is "001" or "ZZ")
        {
            string languageRegion = string.Empty;
            try { languageRegion = new RegionInfo(uiCultureName).TwoLetterISORegionName; }
            catch (ArgumentException) { }
            mainland = languageRegion == "CN" && timeZoneId is "China Standard Time" or "Asia/Shanghai" or "Asia/Urumqi";
        }
        return new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [SettingsRegistry.Keys.DisplayLanguage.Value] = (int)AppLanguage.AutoDetect,
            [SettingsRegistry.Keys.DataUnitStyle.Value] = (int)DataUnitStyle.BinaryWithDecimalLabels,
            [SettingsRegistry.Keys.RateUnit.Value] = (int)RateUnit.Bytes,
            [SettingsRegistry.Keys.ClockFormat.Value] = (int)ClockFormat.FollowSystem,
            [SettingsRegistry.Keys.MainlandChinaFeatureMode.Value] = (int)(mainland
                ? MainlandChinaFeatureMode.FlagReplacementAndTextCompletion : MainlandChinaFeatureMode.Disabled),
            [SettingsRegistry.Keys.MainlandChinaUrlBlockingEnabled.Value] = false,
        };
    }
}
