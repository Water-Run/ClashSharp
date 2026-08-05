using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="LogStorageService"/> to connection logging.</summary>
/// <remarks>
/// Invariants: Wraps a non-null log storage service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Writes application logs to persistent storage.
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
