using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ConnectionSamplingService
{
    private static readonly Lazy<ConnectionSamplingService> SharedSampling = new(ConnectionSamplingServiceFactory.CreateDefault);

    /// <summary>Shared singleton instance created when the host first requests sampling.</summary>
    /// <value>A non-null <see cref="ConnectionSamplingService"/> instance.</value>
    public static ConnectionSamplingService Instance => SharedSampling.Value;

    /// <summary>Returns an existing sampler without constructing settings or storage during partial-startup cleanup.</summary>
    internal static ConnectionSamplingService? GetCreatedInstance() => SharedSampling.IsValueCreated ? SharedSampling.Value : null;
}

/// <summary>Creates connection sampling services with production dependencies.</summary>
internal static class ConnectionSamplingServiceFactory
{
    /// <summary>Creates the default connection sampling service used by application startup and settings.</summary>
    public static ConnectionSamplingService CreateDefault()
    {
        return new ConnectionSamplingService(
            new ConnectionSamplingSettingsAdapter(AppSettingsService.Instance),
            new ConnectionSamplingSourceAdapter(MihomoConnectionService.Instance),
            new ConnectionSamplingStorageAdapter(RuntimeDataServices.Logs),
            LocalizationService.Instance.GetString,
            SystemSupervisorClock.Instance,
            SupervisorBackoffPolicy.CreateProduction("connection-sampling"));
    }
}

internal sealed class ConnectionSamplingSettingsAdapter(AppSettingsService settings) : IConnectionSamplingSettings
{
    public bool IsEnabled => settings.ConnectionSamplingEnabled;

    public int IntervalSeconds => settings.ConnectionSamplingIntervalSeconds;
}

internal sealed class ConnectionSamplingSourceAdapter(MihomoConnectionService connections) : IConnectionSamplingSource
{
    public Task<MihomoTrafficSnapshot?> GetTrafficSnapshotAsync(CancellationToken cancellationToken)
    {
        return connections.TryGetTrafficSnapshotAsync(cancellationToken);
    }
}

internal sealed class ConnectionSamplingStorageAdapter(ILogStorage logStorage) : IConnectionSamplingStorage
{
    public int AppendTrafficSnapshot(MihomoTrafficSnapshot snapshot)
    {
        return logStorage.AppendTrafficSnapshot(snapshot);
    }

    public void AppendLog(string level, string category, string message, string? detail)
    {
        logStorage.AppendLog(level, category, message, detail);
    }
}
