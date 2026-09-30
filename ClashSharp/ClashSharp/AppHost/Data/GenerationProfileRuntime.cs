using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Composes a complete profile runtime request from its owning generation's settings snapshot.</summary>
internal sealed class GenerationProfileRuntime(SettingsAuthoritySession session, CoreConfigurationService configuration,
    NetworkTakeoverService takeover) : IProfileCatalogRuntime
{
    public async Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        SettingsEnvelope snapshot = session.Snapshot;
        var result = await takeover.ApplyProfileConfigurationAsync(configuration, profileId,
            snapshot.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>(),
            snapshot.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>(),
            snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>(), cancellationToken).ConfigureAwait(false);
        return result.IsApplied;
    }

    public async Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(string profileId, string profileName,
        string configurationText, CancellationToken cancellationToken)
    {
        SettingsEnvelope snapshot = session.Snapshot;
        var result = await takeover.ImportAndApplyProfileConfigurationAsync(configuration, profileId, profileName, configurationText,
            snapshot.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>(),
            snapshot.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>(),
            snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>(), cancellationToken).ConfigureAwait(false);
        return new(result.Profile, result.IsApplied);
    }

    public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
        configuration.DeleteImportedProfileAsync(profileId, cancellationToken);
}
