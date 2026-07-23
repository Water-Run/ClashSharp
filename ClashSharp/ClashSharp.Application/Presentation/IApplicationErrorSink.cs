namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Describes one unexpected asynchronous application failure.</summary>
/// <param name="OperationName">Stable operation name used for diagnostics.</param>
/// <param name="Exception">Unexpected exception observed by command infrastructure.</param>
public sealed record ApplicationError(string OperationName, Exception Exception);

/// <summary>Observes unexpected asynchronous failures at the presentation boundary.</summary>
public interface IApplicationErrorSink
{
    /// <summary>Reports one unexpected error without changing its original exception semantics.</summary>
    /// <param name="applicationError">Error context.</param>
    /// <param name="cancellationToken">Cancels sink work only.</param>
    Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken);
}

/// <summary>Fallback sink used only when a presentation composition root has not supplied reporting.</summary>
public sealed class NullApplicationErrorSink : IApplicationErrorSink
{
    private NullApplicationErrorSink()
    {
    }

    /// <summary>Gets the shared no-op sink.</summary>
    public static NullApplicationErrorSink Instance { get; } = new();

    /// <inheritdoc />
    public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(applicationError);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }
}
