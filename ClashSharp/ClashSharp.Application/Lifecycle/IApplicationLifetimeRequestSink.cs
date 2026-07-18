namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Identifies the process-level action requested after host shutdown has unwound.</summary>
public enum ApplicationLifetimeRequestKind
{
    /// <summary>Stop the host and exit the current process.</summary>
    Exit,

    /// <summary>Stop the host, launch a replacement process, and exit the current process.</summary>
    Restart,
}

/// <summary>Represents one idempotent handoff from host-owned work to the App-owned outer lifetime.</summary>
/// <param name="Kind">Requested process-level action.</param>
/// <param name="Source">Stable diagnostic source of the request.</param>
public sealed record ApplicationLifetimeRequest(
    ApplicationLifetimeRequestKind Kind,
    string Source)
{
    /// <summary>Creates an exit request.</summary>
    public static ApplicationLifetimeRequest Exit(string source) =>
        Create(ApplicationLifetimeRequestKind.Exit, source);

    /// <summary>Creates a restart request.</summary>
    public static ApplicationLifetimeRequest Restart(string source) =>
        Create(ApplicationLifetimeRequestKind.Restart, source);

    private static ApplicationLifetimeRequest Create(
        ApplicationLifetimeRequestKind kind,
        string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        return new ApplicationLifetimeRequest(kind, source);
    }
}

/// <summary>Accepts at most one process-lifetime request without stopping or disposing the host.</summary>
public interface IApplicationLifetimeRequestSink
{
    /// <summary>Attempts to hand a request to the outer application lifetime.</summary>
    /// <returns><see langword="true"/> only for the request that won the process-level handoff.</returns>
    bool TryRequest(ApplicationLifetimeRequest request);
}
