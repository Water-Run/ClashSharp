using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Hosting.Settings;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.Service;

public sealed partial class NetworkTakeoverService
{
    /// <summary>Applies an explicit profile and effective TUN plan without legacy preference reads or fallback.</summary>
    internal async Task ApplyNetworkSettingsConfigurationAsync(ICoreConfigurationStore configuration,
        NetworkSettingsConfiguration target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(target);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (target.EffectiveTunEnabled)
            {
                MihomoServiceStatus status = await _mihomoService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (!status.IsKnown || !status.IsInstalled)
                {
                    throw new InvalidOperationException("The requested transparent proxy requires an available installed service.");
                }
            }
            RuntimeConfigurationTransactionResult result = await configuration.ApplyRuntimeConfigurationAsync(
                target.ProfileId, target.Mode, target.EffectiveTunEnabled, target.MixedPort, this, cancellationToken).ConfigureAwait(false);
            if (!result.IsApplied) { throw CreateRuntimeTransactionFailure(result); }
        }
        finally { _transitionGate.Release(); }
    }

    /// <summary>Observes external state without reading preferences, bootstrapping storage, or mutating network state.</summary>
    internal async Task<NetworkSettingsConfiguration> ObserveNetworkSettingsAsync(
        Func<RuntimeConfigurationIntegrityObservation> readIntegrity, Func<WindowsProxyOwnershipObservation> readProxy,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(readIntegrity);
        ArgumentNullException.ThrowIfNull(readProxy);
        await _transitionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RuntimeConfigurationIntegrityObservation integrity = readIntegrity();
            if (!integrity.IsKnown) { throw new InvalidOperationException("The runtime configuration is not independently known."); }
            MihomoServiceStatus service = await _mihomoService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            RuntimeConfigurationActivationPlan? plan = integrity.AppliedPlan;
            if (plan is null)
            {
                if (integrity.AppliedGeneration is not null || integrity.AppliedContentHash is not null
                    || !_core.IsOwnershipKnown || _core.IsRunning || !service.HasReleasedChildOwnership || !readProxy().HasReleasedOwnership
                    || readIntegrity() != integrity)
                {
                    throw new InvalidOperationException("An empty runtime cannot claim a clean inactive network baseline.");
                }
                return new(ClashSharpMode.Disabled, SettingsRegistry.Default.Get(SettingsRegistry.Keys.ActiveProfileId.Value).DefaultValue.Get<string>(),
                    false, SettingsRegistry.Default.Get(SettingsRegistry.Keys.MixedPort.Value).SafeFallback.Get<int>());
            }
            if (integrity.AppliedGeneration is not long generation || generation < 1 || integrity.AppliedContentHash is null)
            {
                throw new InvalidOperationException("The stored configuration has no verified generation identity.");
            }
            // A saved active configuration may outlive its process (for example after a reboot).
            // Independently released owners establish an inactive baseline without rewriting preferences
            // or pretending the stopped core is still applying the saved activation plan.
            if (plan.Mode != ClashSharpMode.Disabled && NetworkSettingsOwnersReleased(service) && readProxy().HasReleasedOwnership)
            {
                service = await _mihomoService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
                if (!NetworkSettingsOwnersReleased(service) || !readProxy().HasReleasedOwnership || readIntegrity() != integrity)
                {
                    throw new InvalidOperationException("The inactive network baseline changed during startup observation.");
                }
                return new(ClashSharpMode.Disabled, plan.ProfileId, false, plan.MixedPort);
            }
            if (!NetworkSettingsOwnerMatches(integrity, service)
                || plan.Mode != ClashSharpMode.Disabled && !await _readiness.MatchesRuntimeConfigurationAsync(plan,
                    generation, integrity.AppliedContentHash, service, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException("The runtime owner or authenticated controller does not match the verified configuration.");
            }
            Guid? readinessSession = service.ServiceSessionId;
            service = await _mihomoService.GetStatusAsync(cancellationToken).ConfigureAwait(false);
            WindowsProxyOwnershipObservation proxy = readProxy();
            bool needsProxy = plan.Mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover && !plan.TunEnabled;
            if (!NetworkSettingsOwnerMatches(integrity, service) || plan.TunEnabled && service.ServiceSessionId != readinessSession
                || !(needsProxy ? proxy.MatchesOwnedProxy(_proxyRecovery.BuildLoopbackProxyServer(plan.MixedPort)) : proxy.HasReleasedOwnership)
                || readIntegrity() != integrity)
            {
                throw new InvalidOperationException("The system proxy or configuration changed during network observation.");
            }
            return new(plan.Mode, plan.ProfileId, plan.TunEnabled, plan.MixedPort);
        }
        finally { _transitionGate.Release(); }
    }

    private bool NetworkSettingsOwnerMatches(RuntimeConfigurationIntegrityObservation integrity, MihomoServiceStatus service)
    {
        RuntimeConfigurationActivationPlan plan = integrity.AppliedPlan!;
        return plan.Mode == ClashSharpMode.Disabled
            ? NetworkSettingsOwnersReleased(service)
            : plan.TunEnabled
                ? !_core.IsRunning && _core.IsOwnershipKnown && service.IsKnown && service.IsReady
                    && service.ServiceSessionId is Guid session && session != Guid.Empty
                    && service.ActiveGeneration == integrity.AppliedGeneration
                    && service.ActiveDataGenerationId == integrity.DataGenerationId
                    && StringComparer.Ordinal.Equals(service.ActiveConfigurationHash, integrity.AppliedContentHash)
                : _core.IsRunning && _core.IsOwnershipKnown && service.HasReleasedChildOwnership;
    }

    private bool NetworkSettingsOwnersReleased(MihomoServiceStatus service) =>
        !_core.IsRunning && _core.IsOwnershipKnown && service.HasReleasedChildOwnership;
}
