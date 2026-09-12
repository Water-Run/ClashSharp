using System.Runtime.ExceptionServices;
using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.Infrastructure.Files;

/// <summary>Promotes already-written core configuration candidates without repeating their surrounding transaction.</summary>
internal static class CoreConfigurationFilePromotion
{
    private const int MaximumRetries = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(20);

    /// <summary>Preserves synchronous caller locks with at most five requested 20 ms retry waits.</summary>
    internal static void Promote(string stagingPath, string targetPath, CancellationToken cancellationToken,
        Action<string, string>? move = null)
    {
        (string source, string target) = ValidatePaths(stagingPath, targetPath);
        move ??= MoveFile;
        ExceptionDispatchInfo? firstFailure = null;
        for (int retries = 0; ; retries++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                move(source, target);
                return;
            }
            catch (Exception failure) when (IsRetryable(failure))
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(failure);
                if (retries == MaximumRetries) { firstFailure.Throw(); }
                if (cancellationToken.CanBeCanceled)
                {
                    if (cancellationToken.WaitHandle.WaitOne(RetryDelay)) { cancellationToken.ThrowIfCancellationRequested(); }
                }
                else
                {
                    Thread.Sleep(RetryDelay);
                }
            }
        }
    }

    /// <summary>Retries only the same atomic promotion and preserves cancellation during its bounded wait.</summary>
    internal static async Task PromoteAsync(string stagingPath, string targetPath, CancellationToken cancellationToken,
        Action<string, string>? move = null)
    {
        (string source, string target) = ValidatePaths(stagingPath, targetPath);
        move ??= MoveFile;
        ExceptionDispatchInfo? firstFailure = null;
        for (int retries = 0; ; retries++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                move(source, target);
                return;
            }
            catch (Exception failure) when (IsRetryable(failure))
            {
                firstFailure ??= ExceptionDispatchInfo.Capture(failure);
                if (retries == MaximumRetries) { firstFailure.Throw(); }
                await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static (string Source, string Target) ValidatePaths(string stagingPath, string targetPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagingPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        string source = Path.GetFullPath(stagingPath);
        string target = Path.GetFullPath(targetPath);
        if (StringComparer.OrdinalIgnoreCase.Equals(source, target)
            || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetDirectoryName(source), Path.GetDirectoryName(target)))
        {
            throw new ArgumentException("Core configuration promotion requires distinct files in the same directory.");
        }
        return (source, target);
    }

    private static void MoveFile(string source, string target) => File.Move(source, target, overwrite: true);

    private static bool IsRetryable(Exception failure) => OperatingSystem.IsWindows()
        && failure is IOException or UnauthorizedAccessException
        && !ExceptionGraphClassifier.IsProcessFatal(failure)
        && failure.HResult is unchecked((int)0x80070005) or unchecked((int)0x80070020) or unchecked((int)0x80070021);
}
