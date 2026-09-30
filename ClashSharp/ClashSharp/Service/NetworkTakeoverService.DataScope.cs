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
        Func<CancellationToken, Task> flushSampling, ILogStorage logs)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(refreshTraffic);
        ArgumentNullException.ThrowIfNull(flushSampling);
        ArgumentNullException.ThrowIfNull(logs);
        return new(new ScopedConfiguration(configuration), _core, _windowsProxy, _mihomoService, _proxyRecovery,
            _readiness, _getString, selections, token => FlushOwnedTrafficAsync(refreshTraffic, flushSampling, logs, token), _transitionGate);
    }

    private async Task FlushOwnedTrafficAsync(Func<CancellationToken, Task> refreshTraffic,
        Func<CancellationToken, Task> flushSampling, ILogStorage logs, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            if (!_core.IsRunning && !(await _mihomoService.GetStatusAsync(deadline.Token).ConfigureAwait(false)).HasRunningChild) { return; }
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
