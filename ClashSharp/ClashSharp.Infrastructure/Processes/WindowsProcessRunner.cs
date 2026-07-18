using System.ComponentModel;
using System.Diagnostics;
using ClashSharp.ApplicationModel.Processes;

namespace ClashSharp.Infrastructure.Processes;

/// <summary>Runs Windows processes with concurrent stream draining and process-tree cleanup.</summary>
public sealed class WindowsProcessRunner : IProcessRunner
{
    private static readonly TimeSpan TerminationGracePeriod = TimeSpan.FromSeconds(5);

    /// <inheritdoc />
    public async Task<ProcessRunResult> RunAsync(
        ProcessRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (cancellationToken.IsCancellationRequested)
        {
            return CreateUnstartedResult(ProcessRunOutcome.Cancelled, null);
        }

        using Process process = new()
        {
            StartInfo = CreateStartInfo(request),
        };
        try
        {
            if (!process.Start())
            {
                return CreateUnstartedResult(ProcessRunOutcome.StartFailed, "Process.Start returned false.");
            }
        }
        catch (Exception exception) when (exception is
            Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return CreateUnstartedResult(ProcessRunOutcome.StartFailed, exception.Message);
        }

        int processId = process.Id;
        using CancellationTokenSource streamCancellation = new();
        Task<string> standardOutput = request.RunElevated
            ? Task.FromResult(string.Empty)
            : process.StandardOutput.ReadToEndAsync(streamCancellation.Token);
        Task<string> standardError = request.RunElevated
            ? Task.FromResult(string.Empty)
            : process.StandardError.ReadToEndAsync(streamCancellation.Token);
        Task processExit = process.WaitForExitAsync(CancellationToken.None);

        using CancellationTokenSource timeoutCancellation = new();
        Task timeout = Task.Delay(request.Timeout, timeoutCancellation.Token);
        TaskCompletionSource<object?> callerCancellation = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration cancellationRegistration = cancellationToken.Register(
            static state => ((TaskCompletionSource<object?>)state!).TrySetResult(null),
            callerCancellation);

        Task winner = await Task.WhenAny(processExit, timeout, callerCancellation.Task).ConfigureAwait(false);
        timeoutCancellation.Cancel();
        if (ReferenceEquals(winner, processExit) || process.HasExited)
        {
            await processExit.ConfigureAwait(false);
            StreamDrainResult streams = await DrainStreamsAsync(
                standardOutput,
                standardError,
                streamCancellation.Token).ConfigureAwait(false);
            return new ProcessRunResult(
                ProcessRunOutcome.Completed,
                process.ExitCode,
                processId,
                streams.StandardOutput,
                streams.StandardError,
                streams.FailureMessage);
        }

        ProcessRunOutcome outcome = cancellationToken.IsCancellationRequested
            ? ProcessRunOutcome.Cancelled
            : ProcessRunOutcome.TimedOut;
        string? cleanupFailure = await TerminateTreeAsync(
            process,
            processExit,
            streamCancellation).ConfigureAwait(false);
        StreamDrainResult terminatedStreams = await DrainStreamsAsync(
            standardOutput,
            standardError,
            streamCancellation.Token).ConfigureAwait(false);
        return new ProcessRunResult(
            outcome,
            null,
            processId,
            terminatedStreams.StandardOutput,
            terminatedStreams.StandardError,
            CombineFailures(cleanupFailure, terminatedStreams.FailureMessage));
    }

    private static ProcessStartInfo CreateStartInfo(ProcessRequest request)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = request.FileName,
            WorkingDirectory = request.WorkingDirectory ?? string.Empty,
            UseShellExecute = request.RunElevated,
            Verb = request.RunElevated ? "runas" : string.Empty,
            CreateNoWindow = !request.RunElevated,
            RedirectStandardOutput = !request.RunElevated,
            RedirectStandardError = !request.RunElevated,
        };
        foreach (string argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }

    private static async Task<string?> TerminateTreeAsync(
        Process process,
        Task processExit,
        CancellationTokenSource streamCancellation)
    {
        string? failure = null;
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            failure = exception.Message;
        }

        try
        {
            await processExit.WaitAsync(TerminationGracePeriod).ConfigureAwait(false);
        }
        catch (TimeoutException exception)
        {
            failure = failure is null ? exception.Message : failure + Environment.NewLine + exception.Message;
            streamCancellation.Cancel();
        }

        return failure;
    }

    private static async Task<StreamDrainResult> DrainStreamsAsync(
        Task<string> standardOutput,
        Task<string> standardError,
        CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(standardOutput, standardError)
                .WaitAsync(TerminationGracePeriod, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is
            TimeoutException or OperationCanceledException or IOException or ObjectDisposedException or InvalidOperationException)
        {
            return new StreamDrainResult(
                standardOutput.IsCompletedSuccessfully ? standardOutput.Result : string.Empty,
                standardError.IsCompletedSuccessfully ? standardError.Result : string.Empty,
                exception.Message);
        }

        return new StreamDrainResult(
            await standardOutput.ConfigureAwait(false),
            await standardError.ConfigureAwait(false),
            null);
    }

    private static string? CombineFailures(string? first, string? second)
    {
        if (first is null)
        {
            return second;
        }

        return second is null ? first : first + Environment.NewLine + second;
    }

    private static ProcessRunResult CreateUnstartedResult(ProcessRunOutcome outcome, string? failureMessage)
    {
        return new ProcessRunResult(outcome, null, 0, string.Empty, string.Empty, failureMessage);
    }

    private sealed record StreamDrainResult(
        string StandardOutput,
        string StandardError,
        string? FailureMessage);
}
