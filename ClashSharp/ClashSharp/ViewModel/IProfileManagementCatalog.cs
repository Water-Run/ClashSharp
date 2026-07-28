using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Profile operations required by the profiles presentation model.</summary>
internal interface IProfileManagementCatalog
{
    IReadOnlyList<ConfigurationProfile> GetProfiles();

    Task<ProfileImportResult> ImportLocalProfileAsync(
        string filePath,
        CancellationToken cancellationToken);

    Task<ProfileImportResult> ValidateProfileAsync(
        ConfigurationProfile profile,
        CancellationToken cancellationToken);

    Task<bool> TrySetActiveProfileAsync(
        string profileId,
        CancellationToken cancellationToken);
}
