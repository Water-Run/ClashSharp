/*
 * Mihomo Profile Parser Service Factory
 * Wires production dependencies for imported profile preview parsing
 *
 * @author: WaterRun
 * @file: Service/MihomoProfileParserServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.IO;

namespace ClashSharp.Service;

public sealed partial class MihomoProfileParserService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="MihomoProfileParserService"/> instance.</value>
    public static MihomoProfileParserService Instance { get; } = MihomoProfileParserServiceFactory.CreateDefault();
}

/// <summary>Creates mihomo profile parser service instances with production dependencies.</summary>
internal static class MihomoProfileParserServiceFactory
{
    /// <summary>Creates the default profile parser service.</summary>
    /// <returns>A parser service wired to region display and localization resources.</returns>
    public static MihomoProfileParserService CreateDefault()
    {
        return new MihomoProfileParserService(
            new MihomoProfileTextSource(AppSettingsService.Instance, CoreConfigurationService.Instance),
            RegionDisplayService.Instance.Resolve,
            LocalizationService.Instance.GetString);
    }
}

internal sealed class MihomoProfileTextSource(
    AppSettingsService settings,
    CoreConfigurationService coreConfiguration) : IMihomoProfileTextSource
{
    public string? TryReadActiveProfileText()
    {
        string activeProfileId = settings.ActiveProfileId;
        if (string.IsNullOrWhiteSpace(activeProfileId) || StringComparer.Ordinal.Equals(activeProfileId, ProfileCatalogIds.BuiltInDirect))
        {
            return null;
        }

        string path = coreConfiguration.GetProfileConfigurationPath(activeProfileId);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }
}
