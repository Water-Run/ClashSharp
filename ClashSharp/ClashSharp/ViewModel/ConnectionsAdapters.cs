/*
 * Connections Adapters
 * Connects active connection view model contracts to application services
 *
 * @author: WaterRun
 * @file: ViewModel/ConnectionsAdapters.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Adapts <see cref="LocalizationService"/> to connections localization.</summary>
/// <remarks>
/// Invariants: Wraps a non-null localization service.
/// Thread safety: Matches the wrapped service and is intended for UI-thread use.
/// Side effects: Reads localized strings.
/// </remarks>
internal sealed class ConnectionsLocalizationAdapter : IConnectionsLocalization
{
    /// <summary>Wrapped localization service.</summary>
    private readonly LocalizationService _localization;

    /// <summary>Initializes a connections localization adapter.</summary>
    /// <param name="localization">Localization service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> is null.</exception>
    public ConnectionsLocalizationAdapter(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Resolved localized string or fallback text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public string GetString(string key)
    {
        return _localization.GetString(key);
    }
}

/// <summary>Adapts <see cref="MihomoConnectionService"/> to active connection reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null connection service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Calls the local mihomo external controller.
/// </remarks>
internal sealed class ActiveConnectionClientAdapter : IActiveConnectionClient
{
    /// <summary>Wrapped connection service.</summary>
    private readonly MihomoConnectionService _connections;

    /// <summary>Initializes an active connection client adapter.</summary>
    /// <param name="connections">Connection service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="connections"/> is null.</exception>
    public ActiveConnectionClientAdapter(MihomoConnectionService connections)
    {
        _connections = connections ?? throw new ArgumentNullException(nameof(connections));
    }

    /// <summary>Gets active connection rows.</summary>
    /// <param name="cancellationToken">Cancels the local API request when requested.</param>
    /// <returns>Active connection rows.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the wrapped service.
    /// Completion semantics: Does not persist returned rows.
    /// </remarks>
    public Task<IReadOnlyList<ActiveConnection>> GetActiveConnectionsAsync(CancellationToken cancellationToken)
    {
        return _connections.GetActiveConnectionsAsync(cancellationToken);
    }
}

/// <summary>Adapts <see cref="LogStorageService"/> to connection logging.</summary>
/// <remarks>
/// Invariants: Wraps a non-null log storage service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Writes snapshots and logs to persistent storage.
/// </remarks>
internal sealed class ConnectionLogAdapter : IConnectionLog
{
    /// <summary>Wrapped log storage service.</summary>
    private readonly LogStorageService _log;

    /// <summary>Initializes a connection log adapter.</summary>
    /// <param name="log">Log storage service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    public ConnectionLogAdapter(LogStorageService log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Appends active connection snapshot rows.</summary>
    /// <param name="connections">Connections to persist. Must not be null.</param>
    /// <returns>Number of inserted rows.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="connections"/> is null.</exception>
    public int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections)
    {
        return _log.AppendConnectionSnapshot(connections);
    }

    /// <summary>Appends one log entry.</summary>
    /// <param name="level">Log level. Must not be null.</param>
    /// <param name="category">Log category. Must not be null.</param>
    /// <param name="message">Log summary. Must not be null.</param>
    /// <param name="detail">Optional detail text.</param>
    public void Append(string level, string category, string message, string? detail)
    {
        _log.AppendLog(level, category, message, detail);
    }
}
