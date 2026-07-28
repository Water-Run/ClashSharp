using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts profile persistence to the profiles presentation contract.</summary>
internal sealed class ProfileManagementCatalogAdapter : IProfileManagementCatalog
{
    private readonly ProfileCatalogService _catalog;

    public ProfileManagementCatalogAdapter(ProfileCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyList<ConfigurationProfile> GetProfiles()
    {
        return _catalog.GetProfiles();
    }

    public Task<ProfileImportResult> ImportLocalProfileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        return _catalog.ImportLocalProfileAsync(filePath, cancellationToken);
    }

    public Task<ProfileImportResult> ValidateProfileAsync(
        ConfigurationProfile profile,
        CancellationToken cancellationToken)
    {
        return _catalog.ValidateProfileAsync(profile, cancellationToken);
    }

    public Task<bool> TrySetActiveProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        return Task.Run(
            () => _catalog.TrySetActiveProfile(profileId),
            cancellationToken);
    }
}
