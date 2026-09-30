using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Reads the owning generation and awaits active-profile commands through the sole settings authority.</summary>
internal sealed class GenerationRepositorySettings(SettingsAuthoritySession session, ISettingsAuthority authority) :
    ICoreConfigurationSettings, IProfileCatalogSettings, IProfileCatalogAdmittedSettings
{
    public bool TransparentProxyEnabled => session.Snapshot.Desired[SettingsRegistry.Keys.TransparentProxyEnabled].Value.Get<bool>();
    public int MixedPort => session.Snapshot.Desired[SettingsRegistry.Keys.MixedPort].Value.Get<int>();
    public string ActiveProfileId => session.Snapshot.Desired[SettingsRegistry.Keys.ActiveProfileId].Value.Get<string>();

    public Task SetActiveProfileAsync(string profileId, CancellationToken cancellationToken) =>
        CommitAsync(profileId, null, cancellationToken);

    public Task SetActiveProfileAdmittedAsync(MutationAdmissionLease admissionLease, string profileId, CancellationToken cancellationToken) =>
        CommitAsync(profileId, admissionLease, cancellationToken);

    private async Task CommitAsync(string profileId, MutationAdmissionLease? lease, CancellationToken cancellationToken)
    {
        if (!authority.CaptureSnapshot().Generation.IsSameGeneration(session.Generation))
        {
            throw new InvalidOperationException("The profile operation no longer owns the active settings generation.");
        }
        SettingNormalizationResult normalized = SettingsRegistry.Default.Get(SettingsRegistry.Keys.ActiveProfileId.Value).Normalize(profileId);
        if (!normalized.IsSuccess) { throw new ArgumentException("The profile identifier is invalid.", nameof(profileId)); }
        SettingValueChange[] changes = [new(SettingsRegistry.Keys.ActiveProfileId, normalized.Value!)];
        SettingsAuthorityResult result = lease is null
            ? await authority.ApplyChangesAsync(changes, Guid.NewGuid(), cancellationToken).ConfigureAwait(false)
            : await authority.ApplyChangesAdmittedAsync(changes, Guid.NewGuid(), lease, cancellationToken).ConfigureAwait(false);
        if (!result.IsSucceeded)
        {
            throw new InvalidOperationException($"The active profile could not be committed and verified: {result.Status} ({result.Code}).");
        }
    }
}
