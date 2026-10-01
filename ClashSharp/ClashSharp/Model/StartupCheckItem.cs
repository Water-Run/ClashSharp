namespace ClashSharp.Model;

/// <summary>One localized startup-health row prepared for presentation.</summary>
/// <param name="IsHealthy">True when the check passes.</param>
/// <param name="Title">Localized check title.</param>
/// <param name="Description">Localized check detail.</param>
/// <remarks>
/// Invariants: Title and description are display-ready and contain no raw exception text.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct StartupCheckItem(
    bool IsHealthy,
    string Title,
    string Description)
{
    /// <summary>Gets the stable check category used for related-page navigation.</summary>
    public StartupCheckKind Kind { get; init; }
    /// <summary>Gets whether an absent configuration is optional rather than an operational fault.</summary>
    public bool IsOptional { get; init; }
    /// <summary>Gets whether the probe could not establish the current state.</summary>
    public bool IsUnavailable { get; init; }
    /// <summary>Gets the display state without turning an unknown probe into a successful check.</summary>
    public StartupCheckState State => IsUnavailable ? StartupCheckState.Unavailable
        : IsHealthy ? StartupCheckState.Healthy : IsOptional ? StartupCheckState.Optional : StartupCheckState.Attention;
}

/// <summary>Stable categories of startup checks.</summary>
public enum StartupCheckKind
{
    /// <summary>A check without a related application page.</summary>
    Unknown,
    /// <summary>Subscription configuration.</summary>
    Subscription,
    /// <summary>Transparent-proxy service availability.</summary>
    TransparentProxy,
    /// <summary>Optional recovery at user sign-in.</summary>
    StartupRecovery,
    /// <summary>Windows system proxy ownership and stale state.</summary>
    SystemProxy,
}

/// <summary>Visual outcome of a startup check.</summary>
public enum StartupCheckState
{
    /// <summary>The check established the expected state.</summary>
    Healthy,
    /// <summary>An optional configuration is not enabled.</summary>
    Optional,
    /// <summary>A known condition requires attention.</summary>
    Attention,
    /// <summary>The probe could not determine the state.</summary>
    Unavailable,
}
