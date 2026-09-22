using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Settings;

namespace ClashSharp.Service;

public sealed partial class ConnectionSamplingService
{
    /// <summary>Applies an explicit pair after draining the old loop; the caller owns the surrounding settings transaction.</summary>
    /// <remarks>Once quiescence starts, retain ownership until the old iteration drains and the new configuration is installed.</remarks>
    internal async Task ApplyConfigurationAsync(ConnectionSamplingSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _supervisor.QuiesceAsync(CancellationToken.None).ConfigureAwait(false);
            _explicitSettings = settings;
            InstallConfiguredInterval();
            if (settings.Enabled) { await _supervisor.StartAsync(CancellationToken.None).ConfigureAwait(false); }
        }
        finally { _configurationGate.Release(); }
    }

    /// <summary>Observes the owned loop and installed interval without consulting persisted desired settings.</summary>
    internal async Task<ConnectionSamplingSettings> ReadConfigurationAsync(CancellationToken cancellationToken)
    {
        await _configurationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return new(_supervisor.IsRunning, Volatile.Read(ref _effectiveIntervalSeconds)); }
        finally { _configurationGate.Release(); }
    }

    private bool IsConfiguredEnabled => _explicitSettings?.Enabled ?? _settings.IsEnabled;

    private void InstallConfiguredInterval() =>
        Volatile.Write(ref _effectiveIntervalSeconds, _explicitSettings?.IntervalSeconds ?? _settings.IntervalSeconds);

    private async Task StartConfiguredLoopAsync(CancellationToken cancellationToken)
    {
        if (!_supervisor.IsRunning) { InstallConfiguredInterval(); }
        await _supervisor.StartAsync(cancellationToken).ConfigureAwait(false);
    }
}
