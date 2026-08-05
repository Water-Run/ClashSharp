using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Commits the durable requested mode, TUN preference, and mixed port after runtime verification.</summary>
internal sealed class LegacyNetworkStateCommitter(AppSettingsService settings) : INetworkStateCommitter
{
    public Task PromoteDesiredAsync(
        NetworkPlan plan,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Intent.Kind == NetworkIntentKind.ModeTransition)
        {
            settings.WriteAdmitted(admissionLease, editor =>
            {
                editor.TransparentProxyEnabled = plan.Intent.TransparentProxyEnabled;
                editor.MixedPort = plan.Intent.MixedPort;
                editor.CurrentMode = plan.Intent.Mode;
            });
        }

        return Task.CompletedTask;
    }

    public Task RestoreBaselineAsync(
        NetworkPlan plan,
        MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Intent.Kind == NetworkIntentKind.ModeTransition)
        {
            LegacyNetworkPlanPersistence.PersistedNetworkPlan persisted =
                LegacyNetworkPlanPersistence.Deserialize(plan.CompensationData);
            settings.WriteAdmitted(admissionLease, editor =>
            {
                editor.TransparentProxyEnabled = persisted.DurableBaselineTransparentProxyEnabled
                    ?? persisted.Baseline.TransparentProxyEnabled;
                editor.MixedPort = persisted.DurableBaselineMixedPort
                    ?? persisted.Baseline.MixedPort;
                editor.CurrentMode = persisted.DurableBaselineMode;
            });
        }

        return Task.CompletedTask;
    }

    public Task VerifyDesiredAsync(NetworkPlan plan, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (plan.Intent.Kind == NetworkIntentKind.ModeTransition
            && (settings.CurrentMode != plan.Intent.Mode
                || settings.TransparentProxyEnabled != plan.Intent.TransparentProxyEnabled
                || settings.MixedPort != plan.Intent.MixedPort))
        {
            throw new InvalidOperationException("The durable desired network settings did not verify.");
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
            bool baselineTransparentProxyEnabled = persisted.DurableBaselineTransparentProxyEnabled
                ?? persisted.Baseline.TransparentProxyEnabled;
            int baselineMixedPort = persisted.DurableBaselineMixedPort
                ?? persisted.Baseline.MixedPort;
            if (settings.CurrentMode != persisted.DurableBaselineMode
                || settings.TransparentProxyEnabled != baselineTransparentProxyEnabled
                || settings.MixedPort != baselineMixedPort)
            {
                throw new InvalidOperationException("The durable baseline network settings did not verify.");
            }
        }

        return Task.CompletedTask;
    }
}
