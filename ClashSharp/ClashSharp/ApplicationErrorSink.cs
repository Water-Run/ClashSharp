using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp;

/// <summary>Writes unexpected presentation failures through the application diagnostic log.</summary>
internal sealed class ApplicationErrorSink : IApplicationErrorSink
{
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;
    private readonly Action<ApplicationError>? _notify;

    public ApplicationErrorSink(
        Action<string, string, string, string?> appendLog,
        Func<string, string> getString,
        Action<ApplicationError>? notify = null)
    {
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _notify = notify;
    }

    /// <inheritdoc />
    public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applicationError);
        cancellationToken.ThrowIfCancellationRequested();
        _appendLog(
            "Error",
            "Application",
            _getString("Application.UnexpectedError"),
            string.Format(
                CultureInfo.InvariantCulture,
                "{0}: {1}",
                applicationError.OperationName,
                applicationError.Exception));
        // Command/session boundaries filter caller cancellation before reporting here. An
        // unexpected cancellation from a timed-out dependency still needs ordinary feedback.
        {
            try { _notify?.Invoke(applicationError); }
            catch (Exception deliveryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(deliveryFailure))
            {
                // Notification transport must not revive an already observed asynchronous failure.
            }
        }
        return Task.CompletedTask;
    }
}
