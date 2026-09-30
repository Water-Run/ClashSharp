using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Composes a complete profile runtime request from its owning generation's settings snapshot.</summary>
internal sealed class GenerationProfileRuntime(SettingsAuthoritySession session, CoreConfigurationService configuration) : IProfileCatalogRuntime
{
    private NetworkTakeoverService? _takeover;

    public void BindTakeover(NetworkTakeoverService takeover)
    {
        ArgumentNullException.ThrowIfNull(takeover);
        if (takeover.DataGenerationId != session.Generation.GenerationId)
        {
            throw new InvalidOperationException("The profile runtime and native takeover belong to different data generations.");
        }
        if (Interlocked.CompareExchange(ref _takeover, takeover, null) is not null)
        {
            throw new InvalidOperationException("The profile runtime already has a native owner.");
        }
    }

    private NetworkTakeoverService Takeover => Volatile.Read(ref _takeover)
        ?? throw new InvalidOperationException("The profile runtime has not been composed with its generation.");

    public async Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        SettingsEnvelope snapshot = session.Snapshot;
        var result = await Takeover.ApplyProfileConfigurationAsync(configuration, profileId,
            snapshot.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>(),
            snapshot.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>(),
            snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>(), cancellationToken).ConfigureAwait(false);
        return result.IsApplied;
    }

    public async Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(string profileId, string profileName,
        string configurationText, CancellationToken cancellationToken)
    {
        SettingsEnvelope snapshot = session.Snapshot;
        var result = await Takeover.ImportAndApplyProfileConfigurationAsync(configuration, profileId, profileName, configurationText,
            snapshot.Desired[SettingsRegistry.Keys.CurrentMode].Value.Get<ClashSharpMode>(),
            snapshot.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>(),
            snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>(), cancellationToken).ConfigureAwait(false);
        return new(result.Profile, result.IsApplied);
    }

    public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
        configuration.DeleteImportedProfileAsync(profileId, cancellationToken);
}
