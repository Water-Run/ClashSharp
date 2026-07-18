namespace ClashSharp.ApplicationModel.Processes;

/// <summary>Describes how a bounded process invocation finished.</summary>
public enum ProcessRunOutcome
{
    /// <summary>The process exited on its own, including with a non-zero code.</summary>
    Completed,

    /// <summary>The configured timeout elapsed and tree termination was requested.</summary>
    TimedOut,

    /// <summary>The caller cancelled and tree termination was requested.</summary>
    Cancelled,

    /// <summary>The process could not be started.</summary>
    StartFailed,
}

/// <summary>Contains the typed outcome and independently captured process streams.</summary>
/// <param name="Outcome">Typed completion outcome.</param>
/// <param name="ExitCode">Natural exit code only for completed processes.</param>
/// <param name="ProcessId">Started process identifier, or zero when start failed.</param>
/// <param name="StandardOutput">Complete captured standard output when redirection is available.</param>
/// <param name="StandardError">Complete captured standard error when redirection is available.</param>
/// <param name="FailureMessage">Optional process-start or cleanup diagnostic.</param>
public sealed record ProcessRunResult(
    ProcessRunOutcome Outcome,
    int? ExitCode,
    int ProcessId,
    string StandardOutput,
    string StandardError,
    string? FailureMessage)
{
    /// <summary>Gets both captured streams without losing their distinct source properties.</summary>
    public string CombinedOutput => StandardOutput + StandardError;
}
