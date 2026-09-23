namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Observes committed executions and durable action outcomes without controlling execution.</summary>
public interface ITriggerExecutionLog
{
    /// <summary>Records execution dispatch when result is null, or one durable action outcome.</summary>
    void Write(TriggerExecution execution, TriggerActionResult? result);

    /// <summary>Reports a logging failure through an independent application diagnostic boundary.</summary>
    Task ReportFailureAsync(Exception exception);
}
