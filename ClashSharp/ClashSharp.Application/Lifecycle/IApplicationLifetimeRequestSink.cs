namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Identifies the process-level action requested after host shutdown has unwound.</summary>
public enum ApplicationLifetimeRequestKind
{
    /// <summary>Stop the host and exit the current process.</summary>
    Exit,

    /// <summary>Stop the host, launch a replacement process, and exit the current process.</summary>
    Restart,
}

/// <summary>Classifies whether a failed outer shutdown is conclusive or may have crossed its commit point.</summary>
public enum ApplicationLifetimeShutdownFailureKind
{
    /// <summary>The runtime reported that shutdown aborted without preparing host disposal.</summary>
    Failed,

    /// <summary>An unexpected exception or cancellation left the shutdown outcome uncertain.</summary>
    Uncertain,
}

/// <summary>Describes whether a durable handoff has a confirmed terminal persistence outcome.</summary>
public enum ApplicationLifetimeTerminalStatePersistence
{
    /// <summary>The request has no durable handoff.</summary>
    NotApplicable,

    /// <summary>No terminal durable state has been confirmed.</summary>
    Unconfirmed,

    /// <summary>A terminal durable state was confirmed by the handoff.</summary>
    Confirmed,
}

/// <summary>Coordinates a durable producer handoff with the App-owned outer lifetime.</summary>
public interface IApplicationLifetimeHandoff
{
    /// <summary>Gets the stable identity used to collapse duplicate publications.</summary>
    string IdempotencyKey { get; }

    /// <summary>Waits until every producer-owned lease has been released.</summary>
    Task WaitForReleaseAsync(CancellationToken cancellationToken);

    /// <summary>Records that the outer runner is about to invoke host shutdown.</summary>
    Task MarkShutdownStartedAsync(CancellationToken cancellationToken);

    /// <summary>Records that host shutdown unwound successfully while services remain available.</summary>
    Task MarkShutdownSucceededAsync(CancellationToken cancellationToken);

    /// <summary>Records a classified host-shutdown failure.</summary>
    Task MarkShutdownFailedAsync(
        ApplicationLifetimeShutdownFailureKind failureKind,
        string diagnosticCode,
        CancellationToken cancellationToken);
}

/// <summary>Represents one idempotent handoff from host-owned work to the App-owned outer lifetime.</summary>
public sealed class ApplicationLifetimeRequest
{
    private int _terminalStatePersistence;

    /// <summary>Initializes one validated process-level request.</summary>
    /// <param name="kind">Requested process-level action.</param>
    /// <param name="source">Stable diagnostic source of the request.</param>
    /// <param name="handoff">Optional durable producer handoff that must release before shutdown.</param>
    public ApplicationLifetimeRequest(
        ApplicationLifetimeRequestKind kind,
        string source,
        IApplicationLifetimeHandoff? handoff = null)
    {
        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(source);
        if (handoff is not null && kind != ApplicationLifetimeRequestKind.Exit)
        {
            throw new ArgumentException("Only exit requests can carry a durable handoff.", nameof(handoff));
        }

        if (handoff is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(handoff.IdempotencyKey, nameof(handoff));
        }

        Kind = kind;
        Source = source;
        Handoff = handoff;
        _terminalStatePersistence = (int)(handoff is null
            ? ApplicationLifetimeTerminalStatePersistence.NotApplicable
            : ApplicationLifetimeTerminalStatePersistence.Unconfirmed);
    }

    /// <summary>Gets the requested process-level action.</summary>
    public ApplicationLifetimeRequestKind Kind { get; }

    /// <summary>Gets the stable diagnostic source.</summary>
    public string Source { get; }

    /// <summary>Gets the optional durable producer handoff.</summary>
    public IApplicationLifetimeHandoff? Handoff { get; }

    /// <summary>Gets whether the durable handoff has confirmed a terminal persistence outcome.</summary>
    public ApplicationLifetimeTerminalStatePersistence TerminalStatePersistence =>
        (ApplicationLifetimeTerminalStatePersistence)Volatile.Read(ref _terminalStatePersistence);

    /// <summary>Creates an exit request.</summary>
    public static ApplicationLifetimeRequest Exit(string source) =>
        Create(ApplicationLifetimeRequestKind.Exit, source, null);

    /// <summary>Creates an exit request backed by a durable producer handoff.</summary>
    public static ApplicationLifetimeRequest Exit(
        string source,
        IApplicationLifetimeHandoff handoff)
    {
        ArgumentNullException.ThrowIfNull(handoff);
        return Create(ApplicationLifetimeRequestKind.Exit, source, handoff);
    }

    /// <summary>Creates a restart request.</summary>
    public static ApplicationLifetimeRequest Restart(string source) =>
        Create(ApplicationLifetimeRequestKind.Restart, source, null);

    private static ApplicationLifetimeRequest Create(
        ApplicationLifetimeRequestKind kind,
        string source,
        IApplicationLifetimeHandoff? handoff)
    {
        return new ApplicationLifetimeRequest(kind, source, handoff);
    }

    internal void ConfirmTerminalStatePersistence()
    {
        if (Handoff is not null)
        {
            Interlocked.Exchange(
                ref _terminalStatePersistence,
                (int)ApplicationLifetimeTerminalStatePersistence.Confirmed);
        }
    }
}

/// <summary>Accepts at most one active process-lifetime request without stopping or disposing the host.</summary>
public interface IApplicationLifetimeRequestSink
{
    /// <summary>Attempts to hand a request to the outer application lifetime.</summary>
    /// <returns>
    /// <see langword="true"/> when the request won a processable handoff or idempotently duplicates
    /// the active, reserved, or terminal durable identity; otherwise <see langword="false"/>.
    /// </returns>
    bool TryRequest(ApplicationLifetimeRequest request);
}
