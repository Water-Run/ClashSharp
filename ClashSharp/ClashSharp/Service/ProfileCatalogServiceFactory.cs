using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
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
        return CreateForDirectory(
            AppDataPathService.ResolveLocalDataDirectory(),
            new ProfileCatalogSettingsAdapter(AppSettingsService.Instance),
            new ProfileCatalogCoreConfigurationAdapter(CoreConfigurationService.Instance),
            new ProfileCatalogRuntimeAdapter(
                AppSettingsService.Instance,
                CoreConfigurationService.Instance,
                NetworkTakeoverService.Instance),
            new ProfileCatalogLogAdapter(LogStorageService.Instance),
            LocalizationService.Instance.GetString,
            LateBoundProfileCatalogMutationCoordinator.Instance);
    }

    /// <summary>Creates one catalog for an explicit data root with dependencies owned by the same host or generation.</summary>
    public static ProfileCatalogService CreateForDirectory(
        string dataDirectory,
        IProfileCatalogSettings settings,
        IProfileCatalogCoreConfiguration coreConfiguration,
        IProfileCatalogRuntime runtime,
        IProfileCatalogLog log,
        Func<string, string> getString,
        IProfileCatalogMutationCoordinator mutationCoordinator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        if (!Path.IsPathFullyQualified(dataDirectory))
        {
            throw new ArgumentException("The catalog data directory must be absolute.", nameof(dataDirectory));
        }

        string root = Path.GetFullPath(dataDirectory);
        return new ProfileCatalogService(
            Path.Combine(root, "ProfileCatalog.json"),
            Path.Combine(root, "mihomo", "history"),
            settings,
            coreConfiguration,
            runtime,
            log,
            getString,
            mutationCoordinator);
    }
}

internal sealed class ProfileCatalogRuntimeAdapter(
    AppSettingsService settings,
    CoreConfigurationService configuration,
    NetworkTakeoverService takeover) : IProfileCatalogRuntime
{
    public async Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        RuntimeConfigurationTransactionResult result = await takeover.ApplyProfileConfigurationAsync(
            configuration,
            profileId,
            settings.CurrentMode,
            settings.TransparentProxyEnabled,
            settings.MixedPort,
            cancellationToken).ConfigureAwait(false);
        return result.IsApplied;
    }

    public async Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken)
    {
        ProfileRuntimeConfigurationTransactionResult result = await takeover
            .ImportAndApplyProfileConfigurationAsync(
                configuration,
                profileId,
                profileName,
                configurationText,
                settings.CurrentMode,
                settings.TransparentProxyEnabled,
                settings.MixedPort,
                cancellationToken)
            .ConfigureAwait(false);
        return new ProfileCatalogRuntimeImportResult(result.Profile, result.IsApplied);
    }

    public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        return configuration.DeleteImportedProfileAsync(profileId, cancellationToken);
    }
}

internal sealed class ProfileCatalogSettingsAdapter(AppSettingsService settings) :
    IProfileCatalogSettings,
    IProfileCatalogAdmittedSettings
{
    public string ActiveProfileId
    {
        get => settings.ActiveProfileId;
        set => settings.ActiveProfileId = value;
    }

    public void SetActiveProfileAdmitted(
        MutationAdmissionLease admissionLease,
        string profileId)
    {
        settings.WriteAdmitted(
            admissionLease,
            editor => editor.ActiveProfileId = profileId);
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

    public Task<string?> ReadImportedProfileConfigurationAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        return coreConfiguration.ReadImportedProfileConfigurationAsync(profileId, cancellationToken);
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
