using System;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Hosting;

/// <summary>Bounds retry attempts when no window remains available to request shutdown again.</summary>
internal sealed class HeadlessShutdownPolicy
{
    private readonly int _maximumAttempts;

    /// <summary>Creates a policy with an explicit, positive attempt bound.</summary>
    /// <param name="maximumAttempts">Maximum number of shutdown attempts before terminal fallback.</param>
    internal HeadlessShutdownPolicy(int maximumAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumAttempts, 1);
        _maximumAttempts = maximumAttempts;
    }

    /// <summary>Attempts shutdown until it succeeds or the configured bound is exhausted.</summary>
    /// <param name="shutdownAttempt">One complete application shutdown attempt.</param>
    /// <param name="reportAttemptException">Optional best-effort observer for recoverable exceptions.</param>
    /// <returns>True when an attempt completed shutdown; otherwise false.</returns>
    internal async Task<bool> TryCompleteAsync(
        Func<Task<bool>> shutdownAttempt,
        Action<Exception>? reportAttemptException = null)
    {
        ArgumentNullException.ThrowIfNull(shutdownAttempt);

        for (int attempt = 0; attempt < _maximumAttempts; attempt++)
        {
            try
            {
                if (await shutdownAttempt())
                {
                    return true;
                }
            }
            catch (Exception exception) when (
                StartupCompletionFailurePolicy.IsRecoverable(exception))
            {
                TryReportException(reportAttemptException, exception);
            }
        }

        return false;
    }

    private static void TryReportException(
        Action<Exception>? reportAttemptException,
        Exception exception)
    {
        if (reportAttemptException is null)
        {
            return;
        }

        try
        {
            reportAttemptException(exception);
        }
        catch (Exception diagnosticException) when (
            StartupCompletionFailurePolicy.IsRecoverable(diagnosticException))
        {
            // Reporting is best effort and must not prevent the bounded terminal policy.
        }
    }
}
