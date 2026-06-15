/*
 * Profile Subscription Link Model
 * Represents one subscription link tracked by Clash#
 *
 * @author: WaterRun
 * @file: Model/ProfileSubscriptionLink.cs
 * @date: 2026-06-15
 */

using System;
using ClashSharp.Service;

namespace ClashSharp.Model;

/// <summary>Represents one subscription link tracked by Clash#.</summary>
/// <param name="Id">Stable link identifier; never null.</param>
/// <param name="Name">User-facing link name; never null.</param>
/// <param name="Uri">Subscription URI display text; never null.</param>
/// <param name="IsEnabled">True when automatic update is enabled for this link.</param>
/// <param name="UpdateIntervalHours">Automatic update interval in hours.</param>
/// <param name="LastUpdatedAt">Last update attempt time.</param>
/// <param name="Status">Current link status display text; never null.</param>
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
    string Status)
{
    /// <summary>Gets the UI-filtered subscription link name.</summary>
    /// <value>Display name after mainland China UI replacement; never null.</value>
    public string NameDisplay => MainlandChinaTextDisplayService.Instance.Apply(Name);

    /// <summary>Gets the UI-filtered subscription URI display text.</summary>
    /// <value>URI display text after mainland China UI replacement; never null.</value>
    public string UriDisplay => MainlandChinaTextDisplayService.Instance.Apply(Uri);

    /// <summary>Gets the UI-filtered subscription status display text.</summary>
    /// <value>Status display text after mainland China UI replacement; never null.</value>
    public string StatusDisplay => MainlandChinaTextDisplayService.Instance.Apply(Status);
}
