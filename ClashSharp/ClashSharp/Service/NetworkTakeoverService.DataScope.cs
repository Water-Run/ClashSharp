using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class NetworkTakeoverService
{
    internal Guid? DataGenerationId => _configuration.DataGenerationId;

    /// <summary>Binds native capabilities to one owned repository set while sharing native transition ordering.</summary>
    internal NetworkTakeoverService BindDataScope(ICoreConfigurationStore configuration,
        INetworkTakeoverProxySelections selections, Func<CancellationToken, Task> refreshTraffic,
        Func<CancellationToken, Task> flushSampling, ILogStorage logs, TimeProvider? timeProvider = null,
        Func<RuntimeConfigurationIntegrityObservation, CancellationToken, Task>? flushObservedSampling = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(refreshTraffic);
        ArgumentNullException.ThrowIfNull(flushSampling);
        ArgumentNullException.ThrowIfNull(logs);
        // The owned clock controls only the final-sample deadline; production uses the system clock.
        TimeProvider clock = timeProvider ?? TimeProvider.System;
        return new(new ScopedConfiguration(configuration), _core, _windowsProxy, _mihomoService, _proxyRecovery,
            _readiness, _getString, selections, token => FlushOwnedTrafficAsync(refreshTraffic, flushSampling, logs, clock, token), _transitionGate,
            flushObservedSampling is null ? null : (baseline, token) => FlushOwnedTrafficAsync(refreshTraffic,
                inner => flushObservedSampling(baseline, inner), logs, clock, token, baseline));
    }

    Task ICoreConfigurationRuntime.PrepareTransitionAsync(RuntimeConfigurationIntegrityObservation baseline, CancellationToken cancellationToken) =>
        _flushObservedTraffic?.Invoke(baseline, cancellationToken) ?? Task.CompletedTask;

    private async Task FlushOwnedTrafficAsync(Func<CancellationToken, Task> refreshTraffic,
        Func<CancellationToken, Task> flushSampling, ILogStorage logs, TimeProvider timeProvider,
        CancellationToken cancellationToken, RuntimeConfigurationIntegrityObservation? baseline = null)
    {
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2), timeProvider);
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            MihomoServiceStatus service = await _mihomoService.GetStatusAsync(deadline.Token).ConfigureAwait(false);
            if (!_core.IsRunning && !service.HasRunningChild) { return; }
            if (baseline is { } observed && (!observed.IsKnown || observed.AppliedPlan is not { } plan
                || !NetworkSettingsOwnerMatches(observed, service)
                || !await _readiness.MatchesRuntimeConfigurationAsync(plan, observed.AppliedGeneration!.Value,
                    observed.AppliedContentHash!, service, deadline.Token).ConfigureAwait(false)))
            {
                throw new InvalidOperationException("The final sample owner does not match the configuration baseline.");
            }
            await refreshTraffic(deadline.Token).ConfigureAwait(false);
            await flushSampling(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure)
            && !ExceptionGraphClassifier.IsCallerCancellation(failure, cancellationToken))
        {
            try { logs.AppendLog("Warning", "MihomoCore", "Final traffic sample was unavailable before a core transition.", "traffic.final_sample_unavailable"); }
            catch (Exception logFailure) when (!ExceptionGraphClassifier.IsProcessFatal(logFailure)) { }
        }
    }

    private sealed class ScopedConfiguration(ICoreConfigurationStore store) : INetworkTakeoverCoreConfiguration
    {
        public Guid? DataGenerationId => store.GetState().DataGenerationId;
        public Task<RuntimeConfigurationTransactionResult> ApplyConfigurationAsync(ClashSharpMode mode,
            bool transparentProxyEnabled, int mixedPort, ICoreConfigurationRuntime runtime, CancellationToken token) =>
            store.ApplyRuntimeConfigurationAsync(mode, transparentProxyEnabled, mixedPort, runtime, token);
    }
}
