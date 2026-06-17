/*
 * Settings Diagnostics Adapters
 * Connects settings diagnostics view model contracts to application services
 *
 * @author: WaterRun
 * @file: ViewModel/SettingsDiagnosticsAdapters.cs
 * @date: 2026-06-17
 */

using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Adapts <see cref="WindowsNetworkDiagnosticService"/> to <see cref="IWindowsDiagnosticsClient"/>.</summary>
internal sealed class WindowsDiagnosticsClient : IWindowsDiagnosticsClient
{
    /// <summary>Underlying Windows diagnostic service.</summary>
    private readonly WindowsNetworkDiagnosticService _diagnostics;

    /// <summary>Initializes the adapter.</summary>
    /// <param name="diagnostics">Diagnostic service. Must not be null.</param>
    public WindowsDiagnosticsClient(WindowsNetworkDiagnosticService diagnostics)
    {
        _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
    }

    public Task<WindowsDiagnosticResult> DiagnoseAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
    {
        return _diagnostics.DiagnoseAsync(target, cancellationToken);
    }

    public Task<WindowsDiagnosticResult> ApplyAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
    {
        return _diagnostics.ApplyAsync(target, cancellationToken);
    }

    public Task<WindowsDiagnosticResult> ResetAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
    {
        return _diagnostics.ResetAsync(target, cancellationToken);
    }
}

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
