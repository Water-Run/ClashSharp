using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Mutates profile sources while retaining the owning generation's independently observed network state.</summary>
internal sealed class GenerationProfileRuntime(SettingsAuthoritySession session, CoreConfigurationService configuration,
    WindowsProxyService proxy) : IProfileCatalogRuntime
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

    public async Task<string> ReadActiveProfileIdAsync(string configuredProfileId, CancellationToken cancellationToken) =>
        (await Takeover.ObserveNetworkSettingsAsync(configuration.ObserveRuntimeConfigurationIntegrity,
            proxy.ObserveOwnership, cancellationToken).ConfigureAwait(false)).ProfileId;

    public async Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        var result = await Takeover.ApplyProfileConfigurationAsync(configuration, profileId,
            proxy.ObserveOwnership, cancellationToken).ConfigureAwait(false);
        return result.IsApplied;
    }

    public async Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(string profileId, string profileName,
        string configurationText, CancellationToken cancellationToken)
    {
        var result = await Takeover.ImportAndApplyProfileConfigurationAsync(configuration, profileId, profileName, configurationText,
            proxy.ObserveOwnership, cancellationToken).ConfigureAwait(false);
        return new(result.Profile, result.IsApplied);
    }

    public Task<ProfileCatalogRuntimeImportResult> ImportProfileContentAsync(string profileId, string profileName,
        string content, bool expectedActive, Func<bool>? mayEditActive,
        Func<CancellationToken, Task<ProfileImportResult>> importInactive, CancellationToken cancellationToken) =>
        Takeover.ImportObservedProfileContentAsync(configuration, profileId, profileName, content,
            mayEditActive, proxy.ObserveOwnership, cancellationToken);

    public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
        Takeover.DeleteObservedProfileAsync(configuration, profileId, proxy.ObserveOwnership, cancellationToken);
}
