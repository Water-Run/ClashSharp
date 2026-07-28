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
    string Description);
