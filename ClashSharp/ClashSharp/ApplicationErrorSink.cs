using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Service;

namespace ClashSharp;

/// <summary>Writes unexpected presentation failures through the application diagnostic log.</summary>
internal sealed class ApplicationErrorSink : IApplicationErrorSink
{
    private readonly Action<string, string, string, string?> _appendLog;
    private readonly Func<string, string> _getString;

    private ApplicationErrorSink(
        Action<string, string, string, string?> appendLog,
        Func<string, string> getString)
    {
        _appendLog = appendLog ?? throw new ArgumentNullException(nameof(appendLog));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
    }

    /// <summary>Creates the production sink without exposing another service singleton.</summary>
    public static IApplicationErrorSink CreateDefault()
    {
        return new ApplicationErrorSink(
            LogStorageService.Instance.AppendLog,
            LocalizationService.Instance.GetString);
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
        return Task.CompletedTask;
    }
}
