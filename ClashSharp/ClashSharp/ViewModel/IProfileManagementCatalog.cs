using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Profile operations required by the profiles presentation model.</summary>
internal interface IProfileManagementCatalog
{
    IReadOnlyList<ConfigurationProfile> GetProfiles();

    IReadOnlyList<ProfileHistoryEntry> GetProfileHistory(string profileId);

    Task<ProfileImportResult> ImportLocalProfileAsync(
        string filePath,
        CancellationToken cancellationToken);

    Task<ProfileImportResult> ValidateProfileAsync(
        ConfigurationProfile profile,
        CancellationToken cancellationToken);

    Task<bool> TrySetActiveProfileAsync(
        string profileId,
        CancellationToken cancellationToken);

    Task<bool> TryRenameProfileAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken);

    Task<bool> TryDeleteProfileAsync(
        string profileId,
        CancellationToken cancellationToken);

    Task<ProfileImportResult> RollbackProfileAsync(
        ProfileHistoryEntry historyEntry,
        CancellationToken cancellationToken);
}
