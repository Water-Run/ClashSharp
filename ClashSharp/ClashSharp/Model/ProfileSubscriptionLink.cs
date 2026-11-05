using System;

namespace ClashSharp.Model;

/// <summary>Represents one subscription link tracked by Clash#.</summary>
/// <param name="Id">Stable link identifier; never null.</param>
/// <param name="Name">User-facing link name; never null.</param>
/// <param name="Uri">Subscription URI display text; never null.</param>
/// <param name="IsEnabled">True when automatic update is enabled for this link.</param>
/// <param name="UpdateIntervalHours">Automatic update interval in hours.</param>
/// <param name="LastUpdatedAt">Last successful update time.</param>
/// <param name="Status">Current link status display text; never null.</param>
/// <param name="Revision">Monotonic editable-definition revision used to reject stale imports.</param>
/// <param name="Usage">Last accepted provider metadata, or null when unavailable.</param>
/// <param name="UseProviderName">True for automatic provider naming, false for an explicit user name; null preserves legacy host-name inference.</param>
/// <remarks>
/// Invariants: String values are never null; update interval is positive.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ProfileSubscriptionLink(
    string Id,
    string Name,
    string Uri,
    bool IsEnabled,
    int UpdateIntervalHours,
    DateTimeOffset LastUpdatedAt,
    string Status,
    long Revision = 0,
    SubscriptionUsage? Usage = null,
    bool? UseProviderName = null)
{
    /// <summary>Compares canonical HTTP request addresses, retaining user information and path/query case.</summary>
    /// <param name="first">First saved or entered address.</param>
    /// <param name="second">Second saved or entered address.</param>
    /// <returns>Whether both addresses identify the same request, excluding fragments.</returns>
    public static bool HasSameUri(string first, string second) =>
        System.Uri.TryCreate(first, UriKind.Absolute, out System.Uri? left)
        && System.Uri.TryCreate(second, UriKind.Absolute, out System.Uri? right)
        && StringComparer.Ordinal.Equals(
            left.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped),
            right.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment, UriFormat.UriEscaped));
}
