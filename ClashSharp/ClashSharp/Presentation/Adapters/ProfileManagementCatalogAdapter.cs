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

    public IReadOnlyList<ProfileHistoryEntry> GetProfileHistory(string profileId)
    {
        return _catalog.GetProfileHistory(profileId);
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
        return _catalog.TryApplyActiveProfileAsync(profileId, cancellationToken);
    }

    public Task<bool> TryRenameProfileAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken)
    {
        return _catalog.TryRenameProfileAsync(profileId, name, cancellationToken);
    }

    public Task<bool> TryDeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        return _catalog.TryDeleteProfileAsync(profileId, cancellationToken);
    }

    public Task<ProfileImportResult> RollbackProfileAsync(
        ProfileHistoryEntry historyEntry,
        CancellationToken cancellationToken)
    {
        return _catalog.RollbackProfileAsync(historyEntry, cancellationToken);
    }
}
