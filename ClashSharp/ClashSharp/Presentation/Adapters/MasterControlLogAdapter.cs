using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="ILogStorage"/> to master-control logging.</summary>
/// <remarks>
/// Invariants: Wraps a non-null log service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Writes log entries to persistent storage.
/// </remarks>
internal sealed class MasterControlLogAdapter : IMasterControlLog
{
    /// <summary>Wrapped log service.</summary>
    private readonly ILogStorage _log;

    /// <summary>Initializes a master-control log adapter.</summary>
    /// <param name="log">Log service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="log"/> is null.</exception>
    public MasterControlLogAdapter(ILogStorage log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Appends one log entry.</summary>
    /// <param name="level">Log level. Must not be null.</param>
    /// <param name="category">Log category. Must not be null.</param>
    /// <param name="message">Log summary. Must not be null.</param>
    /// <param name="detail">Optional detail text; null when no detail exists.</param>
    public void Append(string level, string category, string message, string? detail)
    {
        _log.AppendLog(level, category, message, detail);
    }
}
