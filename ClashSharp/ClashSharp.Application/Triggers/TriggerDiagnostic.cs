namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies trigger diagnostic severity.</summary>
public enum TriggerDiagnosticSeverity
{
    /// <summary>Informational state that needs no intervention.</summary>
    Information = 0,

    /// <summary>Recoverable degraded state.</summary>
    Warning = 1,

    /// <summary>Failure that blocks a sound operation.</summary>
    Error = 2,
}

/// <summary>One immutable trigger diagnostic suitable for persistence and presentation.</summary>
public sealed record TriggerDiagnostic
{
    /// <summary>Initializes one validated trigger diagnostic.</summary>
    /// <param name="code">Machine-stable diagnostic code.</param>
    /// <param name="severity">Diagnostic severity.</param>
    /// <param name="taskId">Affected task identity, or null for repository-wide state.</param>
    /// <param name="detail">Nonlocalized diagnostic detail.</param>
    /// <param name="occurredAt">Timestamp at which the state was observed.</param>
    public TriggerDiagnostic(
        string code,
        TriggerDiagnosticSeverity severity,
        string? taskId,
        string detail,
        DateTimeOffset occurredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (!Enum.IsDefined(severity))
        {
            throw new ArgumentOutOfRangeException(nameof(severity));
        }

        if (taskId is not null && string.IsNullOrWhiteSpace(taskId))
        {
            throw new ArgumentException("Task identity must be null or nonempty.", nameof(taskId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(detail);
        Code = code;
        Severity = severity;
        TaskId = taskId;
        Detail = detail;
        OccurredAt = occurredAt;
    }

    /// <summary>Gets the machine-stable diagnostic code.</summary>
    public string Code { get; }

    /// <summary>Gets the diagnostic severity.</summary>
    public TriggerDiagnosticSeverity Severity { get; }

    /// <summary>Gets the affected task identity, or null for repository-wide state.</summary>
    public string? TaskId { get; }

    /// <summary>Gets the nonlocalized diagnostic detail.</summary>
    public string Detail { get; }

    /// <summary>Gets the timestamp at which the state was observed.</summary>
    public DateTimeOffset OccurredAt { get; }
}
