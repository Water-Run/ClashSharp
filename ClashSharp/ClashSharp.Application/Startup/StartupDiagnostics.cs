namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Identifies the lifecycle point represented by a startup diagnostic.</summary>
public enum StartupDiagnosticStage
{
    /// <summary>The startup step is about to execute.</summary>
    Started,

    /// <summary>The startup step returned a typed outcome.</summary>
    Completed,

    /// <summary>The startup step threw before returning an outcome.</summary>
    Failed,
}

/// <summary>Describes one persistable startup pipeline observation.</summary>
/// <param name="StepName">Stable startup step name.</param>
/// <param name="StepOrder">Configured startup step order.</param>
/// <param name="Stage">Observed lifecycle stage.</param>
/// <param name="Outcome">Typed result for a completed step; otherwise null.</param>
/// <param name="DiagnosticCode">Stable result code when supplied by the step.</param>
/// <param name="Elapsed">Elapsed execution time at the observation point.</param>
/// <param name="ExceptionType">Thrown exception type for a failed step.</param>
/// <param name="ExceptionMessage">Thrown exception message for a failed step.</param>
public sealed record StartupDiagnosticRecord(
    string StepName,
    int StepOrder,
    StartupDiagnosticStage Stage,
    StartupStepOutcome? Outcome,
    string? DiagnosticCode,
    TimeSpan Elapsed,
    string? ExceptionType,
    string? ExceptionMessage);

/// <summary>Persists startup diagnostics without owning startup control flow.</summary>
public interface IStartupDiagnosticSink
{
    /// <summary>Records one startup observation.</summary>
    /// <param name="record">Complete diagnostic record.</param>
    void Record(StartupDiagnosticRecord record);

    /// <summary>
    /// Records a failed startup observation while allowing an asynchronous sink to defer
    /// exception-controlled text access.
    /// </summary>
    /// <param name="record">Failure metadata that is safe to capture synchronously.</param>
    /// <param name="exception">Original failure. The caller must not read its text.</param>
    void RecordFailure(StartupDiagnosticRecord record, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Record(record);
    }
}
