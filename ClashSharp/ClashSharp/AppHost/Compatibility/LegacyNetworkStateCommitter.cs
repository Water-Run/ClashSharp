using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Commits only the durable selected mode after the external network target verifies.</summary>
internal sealed class LegacyNetworkStateCommitter(AppSettingsService settings) : INetworkStateCommitter
{
    public Task PromoteDesiredAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Intent.Kind == NetworkIntentKind.ModeTransition)
        {
            settings.CurrentMode = plan.Intent.Mode;
        }

        return Task.CompletedTask;
    }

    public Task RestoreBaselineAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Intent.Kind == NetworkIntentKind.ModeTransition)
        {
            LegacyNetworkPlanPersistence.PersistedNetworkPlan persisted =
                LegacyNetworkPlanPersistence.Deserialize(plan.CompensationData);
            settings.CurrentMode = persisted.DurableBaselineMode;
        }

        return Task.CompletedTask;
    }

    public Task VerifyDesiredAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Intent.Kind == NetworkIntentKind.ModeTransition && settings.CurrentMode != plan.Intent.Mode)
        {
            throw new InvalidOperationException("The durable desired network mode did not verify.");
        }

        return Task.CompletedTask;
    }

    public Task VerifyBaselineAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Intent.Kind == NetworkIntentKind.ModeTransition)
        {
            LegacyNetworkPlanPersistence.PersistedNetworkPlan persisted =
                LegacyNetworkPlanPersistence.Deserialize(plan.CompensationData);
            if (settings.CurrentMode != persisted.DurableBaselineMode)
            {
                throw new InvalidOperationException("The durable baseline network mode did not verify.");
            }
        }

        return Task.CompletedTask;
    }
}
