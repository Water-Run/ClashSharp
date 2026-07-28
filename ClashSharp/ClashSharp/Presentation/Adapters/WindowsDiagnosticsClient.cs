using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

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

    public Task<WindowsDiagnosticResult> DiagnoseAsync(
        WindowsDiagnosticTarget target,
        CancellationToken cancellationToken)
    {
        return _diagnostics.DiagnoseAsync(target, cancellationToken);
    }

    public Task<WindowsDiagnosticResult> ApplyAsync(
        WindowsDiagnosticTarget target,
        CancellationToken cancellationToken)
    {
        return _diagnostics.ApplyAsync(target, cancellationToken);
    }

    public Task<WindowsDiagnosticResult> ResetAsync(
        WindowsDiagnosticTarget target,
        CancellationToken cancellationToken)
    {
        return _diagnostics.ResetAsync(target, cancellationToken);
    }
}
