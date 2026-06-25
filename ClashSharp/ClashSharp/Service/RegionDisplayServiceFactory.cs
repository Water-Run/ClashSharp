/*
 * Region Display Service Factory
 * Wires production dependencies for region display resolution
 *
 * @author: WaterRun
 * @file: Service/RegionDisplayServiceFactory.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Creates region display service instances with production dependencies.</summary>
internal static class RegionDisplayServiceFactory
{
    /// <summary>Creates the default region display service used by the application singleton.</summary>
    /// <returns>A region display service wired to app settings and localization resources.</returns>
    public static RegionDisplayService CreateDefault()
    {
        return new RegionDisplayService(GetFeatureMode, LocalizationService.Instance.GetString);
    }

    private static MainlandChinaFeatureMode GetFeatureMode()
    {
        return AppSettingsService.Instance.MainlandChinaFeatureMode;
    }
}
