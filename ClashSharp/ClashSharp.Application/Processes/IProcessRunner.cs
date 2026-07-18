namespace ClashSharp.ApplicationModel.Processes;

/// <summary>Runs external processes with bounded, typed completion semantics.</summary>
public interface IProcessRunner
{
    /// <summary>Runs one request and always distinguishes completion, timeout, cancellation, and start failure.</summary>
    /// <param name="request">Immutable process request.</param>
    /// <param name="cancellationToken">Caller cancellation signal.</param>
    /// <returns>The typed process result after exit and available stream draining.</returns>
    Task<ProcessRunResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken);
}
