namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Delivers one idempotent notification for a durably committed trigger execution.</summary>
public interface ITriggerFiredNotificationSink
{
    /// <summary>Notifies that one committed execution fired before its recoverable actions run.</summary>
    Task NotifyAsync(TriggerExecution execution, CancellationToken cancellationToken);

    /// <summary>Records a contained delivery failure without throwing into the business outbox.</summary>
    void ReportFailure(TriggerExecution execution, Exception exception);
}

/// <summary>Explicit null object for hosts and tests that intentionally omit fired notifications.</summary>
public sealed class NullTriggerFiredNotificationSink : ITriggerFiredNotificationSink
{
    private NullTriggerFiredNotificationSink()
    {
    }

    /// <summary>Gets the shared stateless null sink.</summary>
    public static NullTriggerFiredNotificationSink Instance { get; } = new();

    /// <inheritdoc />
    public Task NotifyAsync(
        TriggerExecution execution,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(execution);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void ReportFailure(TriggerExecution execution, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(execution);
        ArgumentNullException.ThrowIfNull(exception);
    }
}
