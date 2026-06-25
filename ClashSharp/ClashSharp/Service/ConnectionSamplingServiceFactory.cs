/*
 * Connection Sampling Service Factory
 * Wires production dependencies for background connection sampling
 *
 * @author: WaterRun
 * @file: Service/ConnectionSamplingServiceFactory.cs
 * @date: 2026-06-25
 */

using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ConnectionSamplingService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ConnectionSamplingService"/> instance.</value>
    public static ConnectionSamplingService Instance { get; } = ConnectionSamplingServiceFactory.CreateDefault();
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
            new ConnectionSamplingStorageAdapter(LogStorageService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class ConnectionSamplingSettingsAdapter(AppSettingsService settings) : IConnectionSamplingSettings
{
    public bool IsEnabled => settings.ConnectionSamplingEnabled;

    public int IntervalSeconds => settings.ConnectionSamplingIntervalSeconds;
}

internal sealed class ConnectionSamplingSourceAdapter(MihomoConnectionService connections) : IConnectionSamplingSource
{
    public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        return connections.GetActiveConnectionsAsync(cancellationToken);
    }
}

internal sealed class ConnectionSamplingStorageAdapter(LogStorageService logStorage) : IConnectionSamplingStorage
{
    public int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections)
    {
        return logStorage.AppendConnectionSnapshot(connections);
    }

    public void AppendLog(string level, string category, string message, string? detail)
    {
        logStorage.AppendLog(level, category, message, detail);
    }
}
