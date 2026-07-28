using System;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="LogStorageService"/> to <see cref="IDiagnosticsLog"/>.</summary>
internal sealed class DiagnosticsLog : IDiagnosticsLog
{
    /// <summary>Underlying application log storage.</summary>
    private readonly LogStorageService _logStorage;

    /// <summary>Initializes the adapter.</summary>
    /// <param name="logStorage">Log storage service. Must not be null.</param>
    public DiagnosticsLog(LogStorageService logStorage)
    {
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
    }

    public void Append(string level, string category, string message, string? detail)
    {
        _logStorage.AppendLog(level, category, message, detail);
    }
}
