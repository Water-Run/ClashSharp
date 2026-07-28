using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Components;
using ClashSharp.Model;
using ClashSharp.Service;
using Microsoft.UI.Xaml;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Shows the startup guide for one presentation-owned visual lifetime.</summary>
internal interface IStartupGuidePresenter
{
    /// <summary>Collects one health snapshot and presents it in the supplied window root.</summary>
    Task ShowAsync(XamlRoot xamlRoot, CancellationToken cancellationToken);
}

/// <summary>Coordinates asynchronous startup checks and the startup-guide content dialog.</summary>
/// <remarks>
/// The presenter is the single owner of startup-guide presentation. The component receives only
/// already-collected rows and display dependencies, while the per-window coordinator owns modal
/// admission and cancellation.
/// </remarks>
internal sealed class StartupGuidePresenter : IStartupGuidePresenter
{
    private readonly StartupCheckService _checks;
    private readonly Func<string, string> _getString;
    private readonly IApplicationErrorSink _errorSink;
    private int _isPresentationActive;

    public StartupGuidePresenter(
        StartupCheckService checks,
        Func<string, string> getString,
        IApplicationErrorSink errorSink)
    {
        _checks = checks ?? throw new ArgumentNullException(nameof(checks));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
    }

    /// <inheritdoc />
    public async Task ShowAsync(
        XamlRoot xamlRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(xamlRoot);
        if (Interlocked.CompareExchange(ref _isPresentationActive, 1, 0) != 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<StartupCheckItem> checks =
                await _checks.GetChecksAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();

            StartupGuideDialog dialog = new(checks, _getString)
            {
                XamlRoot = xamlRoot,
            };
            _ = await WindowDialogCoordinator.ShowAsync(dialog, cancellationToken);
        }
        catch (Exception exception) when (
            ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            // Closing the page or window dismisses its presentation without surfacing an error.
        }
        catch (Exception exception) when (
            !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportUnexpectedAsync(exception);
        }
        finally
        {
            Volatile.Write(ref _isPresentationActive, 0);
        }
    }

    private async Task ReportUnexpectedAsync(Exception exception)
    {
        try
        {
            await _errorSink.ReportAsync(
                new ApplicationError("startup-guide-presentation", exception),
                CancellationToken.None);
        }
        catch (Exception sinkException) when (
            !ExceptionGraphClassifier.IsProcessFatal(sinkException))
        {
            // The primary diagnostic sink has no independent fallback channel.
        }
    }
}
