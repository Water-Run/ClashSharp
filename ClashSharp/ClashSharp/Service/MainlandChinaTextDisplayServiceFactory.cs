/*
 * Mainland China Text Display Service Factory
 * Wires production dependencies for UI-only mainland China display filtering
 *
 * @author: WaterRun
 * @file: Service/MainlandChinaTextDisplayServiceFactory.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Creates mainland China text display service instances with production dependencies.</summary>
internal static class MainlandChinaTextDisplayServiceFactory
{
    /// <summary>Creates the default mainland China text display service used by the application singleton.</summary>
    /// <returns>A display service wired to app settings.</returns>
    public static MainlandChinaTextDisplayService CreateDefault()
    {
        return new MainlandChinaTextDisplayService(GetFeatureMode, GetUrlBlockingEnabled);
    }

    private static MainlandChinaFeatureMode GetFeatureMode()
    {
        return AppSettingsService.Instance.MainlandChinaFeatureMode;
    }

    private static bool GetUrlBlockingEnabled()
    {
        return AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled;
    }
}
