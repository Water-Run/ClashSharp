/*
 * Profile Catalog Service Factory
 * Wires production dependencies for local profile catalog management
 *
 * @author: WaterRun
 * @file: Service/ProfileCatalogServiceFactory.cs
 * @date: 2026-06-25
 */

using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ProfileCatalogService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProfileCatalogService"/> instance.</value>
    public static ProfileCatalogService Instance { get; } = ProfileCatalogServiceFactory.CreateDefault();
}

/// <summary>Creates profile catalog services with production dependencies.</summary>
internal static class ProfileCatalogServiceFactory
{
    /// <summary>Creates the default service used by profiles, links, statistics, and maintenance flows.</summary>
    public static ProfileCatalogService CreateDefault()
    {
        return new ProfileCatalogService(
            Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "ProfileCatalog.json"),
            new ProfileCatalogSettingsAdapter(AppSettingsService.Instance),
            new ProfileCatalogCoreConfigurationAdapter(CoreConfigurationService.Instance),
            new ProfileCatalogLogAdapter(LogStorageService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class ProfileCatalogSettingsAdapter(AppSettingsService settings) : IProfileCatalogSettings
{
    public string ActiveProfileId
    {
        get => settings.ActiveProfileId;
        set => settings.ActiveProfileId = value;
    }
}

internal sealed class ProfileCatalogCoreConfigurationAdapter(CoreConfigurationService coreConfiguration) : IProfileCatalogCoreConfiguration
{
    public Task<ProfileImportResult> ImportProfileConfigurationAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken)
    {
        return coreConfiguration.ImportProfileConfigurationAsync(profileId, profileName, configurationText, cancellationToken);
    }

    public CoreConfigurationState EnsureDefaultConfiguration()
    {
        return coreConfiguration.EnsureDefaultConfiguration();
    }

    public Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        return coreConfiguration.ValidateImportedProfileAsync(profileId, cancellationToken);
    }
}

internal sealed class ProfileCatalogLogAdapter(LogStorageService logStorage) : IProfileCatalogLog
{
    public void AppendLog(string level, string category, string message, string? detail)
    {
        logStorage.AppendLog(level, category, message, detail);
    }
}
