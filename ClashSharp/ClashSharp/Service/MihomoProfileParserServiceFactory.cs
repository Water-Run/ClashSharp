using System;

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
            new MihomoProfileTextSource(RuntimeDataServices.Configuration),
            RegionDisplayService.Instance.Resolve,
            LocalizationService.Instance.GetString);
    }
}

internal sealed class MihomoProfileTextSource(
    ICoreConfigurationStore coreConfiguration) : IMihomoProfileTextSource
{
    public string? TryReadActiveProfileText()
    {
        string activeProfileId = coreConfiguration.ObserveRuntimeConfigurationIntegrity() is { IsKnown: true, AppliedPlan: { } plan }
            ? plan.ProfileId : string.Empty;
        if (string.IsNullOrWhiteSpace(activeProfileId) || StringComparer.Ordinal.Equals(activeProfileId, ProfileCatalogIds.BuiltInDirect))
        {
            return null;
        }

        return coreConfiguration.TryReadProfileConfigurationText(activeProfileId, out string? text) ? text : null;
    }
}
