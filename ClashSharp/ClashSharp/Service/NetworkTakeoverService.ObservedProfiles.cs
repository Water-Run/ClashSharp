using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Hosting.Settings;

namespace ClashSharp.Service;

public sealed partial class NetworkTakeoverService
{
    /// <summary>Changes only the selected profile while preserving the observed native network configuration.</summary>
    internal Task<RuntimeConfigurationTransactionResult> ApplyProfileConfigurationAsync(
        ICoreConfigurationStore configuration, string profileId, Func<WindowsProxyOwnershipObservation> readProxy,
        CancellationToken cancellationToken) =>
        ExecuteObservedProfileMutationAsync(configuration, readProxy,
            before => configuration.ApplyRuntimeConfigurationAsync(profileId, before.Mode, before.EffectiveTunEnabled,
                before.MixedPort, this, cancellationToken), cancellationToken);

    /// <summary>Restores an already authorized source using the currently observed network configuration.</summary>
    internal Task<ProfileRuntimeConfigurationTransactionResult> ImportAndApplyProfileConfigurationAsync(
        ICoreConfigurationStore configuration, string profileId, string profileName, string content,
        Func<WindowsProxyOwnershipObservation> readProxy, CancellationToken cancellationToken) =>
        ExecuteObservedProfileMutationAsync(configuration, readProxy,
            before => configuration.ImportAndApplyProfileConfigurationAsync(profileId, profileName, content,
                before.Mode, before.EffectiveTunEnabled, before.MixedPort, this, cancellationToken), cancellationToken);

    /// <summary>Classifies and authorizes a source edit under the same gate that excludes profile activation.</summary>
    internal Task<ProfileCatalogRuntimeImportResult> ImportObservedProfileContentAsync(
        ICoreConfigurationStore configuration, string profileId, string profileName, string content,
        Func<bool>? mayEditActive, Func<WindowsProxyOwnershipObservation> readProxy, CancellationToken cancellationToken) =>
        ExecuteObservedProfileMutationAsync(configuration, readProxy, async before =>
        {
            bool active = StringComparer.Ordinal.Equals(before.ProfileId, profileId);
            if (active && mayEditActive is not null && !mayEditActive())
            {
                throw new UnauthorizedAccessException("Active profile editing requires proxy control.");
            }
            if (!active)
            {
                return new ProfileCatalogRuntimeImportResult(await configuration.ImportProfileConfigurationAsync(
                    profileId, profileName, content, cancellationToken).ConfigureAwait(false), true);
            }
            ProfileRuntimeConfigurationTransactionResult applied = await configuration.ImportAndApplyProfileConfigurationAsync(
                profileId, profileName, content, before.Mode, before.EffectiveTunEnabled, before.MixedPort,
                this, cancellationToken).ConfigureAwait(false);
            return new ProfileCatalogRuntimeImportResult(applied.Profile, applied.IsApplied) { WasActive = true };
        }, cancellationToken);

    /// <summary>Rejects deletion while the source belongs to the currently installed profile.</summary>
    internal Task<bool> DeleteObservedProfileAsync(ICoreConfigurationStore configuration, string profileId,
        Func<WindowsProxyOwnershipObservation> readProxy, CancellationToken cancellationToken) =>
        ExecuteObservedProfileMutationAsync(configuration, readProxy, before =>
        {
            if (StringComparer.Ordinal.Equals(before.ProfileId, profileId))
            {
                throw new InvalidOperationException("The installed profile source cannot be deleted.");
            }
            return configuration.DeleteImportedProfileAsync(profileId, cancellationToken);
        }, cancellationToken);

    // Observation and source promotion retain native ordering, including inactive edits and compensation.
    private async Task<T> ExecuteObservedProfileMutationAsync<T>(ICoreConfigurationStore configuration,
        Func<WindowsProxyOwnershipObservation> readProxy, Func<NetworkSettingsConfiguration, Task<T>> mutation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(readProxy);
        ArgumentNullException.ThrowIfNull(mutation);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            NetworkSettingsConfiguration before = await ObserveNetworkSettingsCoreAsync(
                configuration.ObserveRuntimeConfigurationIntegrity, readProxy, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return await mutation(before).ConfigureAwait(false);
        }
        finally { _transitionGate.Release(); }
    }

}
