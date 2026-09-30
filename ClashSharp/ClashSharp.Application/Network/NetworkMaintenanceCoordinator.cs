using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Network;

/// <summary>Drains ordinary settings and runtime commands before observing and repairing proxy state.</summary>
public sealed class NetworkMaintenanceCoordinator(NetworkStateCoordinator network, MutationAdmissionBarrier admission)
{
    /// <summary>Runs startup proxy recovery or an explicit proxy repair under one exclusive owner.</summary>
    /// <param name="createIntent">Reads the latest settings only after admitted commands have completed.</param>
    /// <param name="cancellationToken">Cancels waiting and planning, without detaching accepted mutation work.</param>
    public async Task<MutationResult<NetworkTransitionResult>> ApplyAsync(
        Func<NetworkIntent> createIntent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(createIntent);
        await using MutationAdmissionLease lease = await admission.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive, cancellationToken).ConfigureAwait(false);
        return await network.ApplyAdmittedAsync(() =>
        {
            NetworkIntent intent = createIntent();
            if (intent.Kind is not (NetworkIntentKind.StartupProxyRecovery or NetworkIntentKind.ProxyConflictRepair))
            {
                throw new ArgumentException("Maintenance only accepts proxy recovery or conflict repair.", nameof(createIntent));
            }
            return intent;
        }, lease, cancellationToken).ConfigureAwait(false);
    }
}
