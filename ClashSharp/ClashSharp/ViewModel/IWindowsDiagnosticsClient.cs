using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Runs Windows-native diagnostic operations for the settings diagnostics view model.</summary>
internal interface IWindowsDiagnosticsClient
{
    Task<WindowsDiagnosticResult> DiagnoseAsync(
        WindowsDiagnosticTarget target,
        CancellationToken cancellationToken);

    Task<WindowsDiagnosticResult> ApplyAsync(
        WindowsDiagnosticTarget target,
        CancellationToken cancellationToken);

    Task<WindowsDiagnosticResult> ResetAsync(
        WindowsDiagnosticTarget target,
        CancellationToken cancellationToken);
}
