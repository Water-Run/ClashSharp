using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Connects sampling transactions to the sole settings authority and supervised loop.</summary>
internal sealed class ConnectionSamplingSettingsOperationAdapter : IConnectionSamplingSettingsOperation
{
    private readonly AppSettingsService _settings;
    private readonly ConnectionSamplingService _sampling;

    public ConnectionSamplingSettingsOperationAdapter(AppSettingsService settings, ConnectionSamplingService sampling)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
    }

    public ConnectionSamplingSettings ReadSettings() => _settings.ReadConnectionSamplingSettings();

    public void WriteSettings(ConnectionSamplingSettings settings, MutationAdmissionLease admissionLease) =>
        _settings.WriteAdmitted(admissionLease, editor =>
        {
            editor.ConnectionSamplingEnabled = settings.Enabled;
            editor.ConnectionSamplingIntervalSeconds = settings.IntervalSeconds;
        });

    public bool IsRunning => _sampling.IsRunning;

    public async Task QuiesceAsync(CancellationToken cancellationToken) =>
        _ = await _sampling.QuiesceAsync(cancellationToken);

    public Task StartAsync(CancellationToken cancellationToken) => _sampling.StartLoopAsync(cancellationToken);
}
