/*
 * Configuration Profile Model
 * Represents one Clash# configuration profile available to the profile page
 *
 * @author: WaterRun
 * @file: Model/ConfigurationProfile.cs
 * @date: 2026-06-15
 */

using System;
using ClashSharp.Service;

namespace ClashSharp.Model;

/// <summary>Represents one Clash# configuration profile available to the profile page.</summary>
/// <param name="Id">Stable profile identifier; never null.</param>
/// <param name="Name">Profile display name; never null.</param>
/// <param name="SourceName">Subscription or local source display name; never null.</param>
/// <param name="Status">Validation and update status display text; never null.</param>
/// <param name="UpdatedAt">Last successful update time.</param>
/// <param name="NodeCount">Number of proxy nodes discovered in the profile.</param>
/// <param name="RuleCount">Number of routing rules discovered in the profile.</param>
/// <param name="IsActive">True when this profile is the active profile.</param>
/// <remarks>
/// Invariants: String values are never null; count values are non-negative.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ConfigurationProfile(
    string Id,
    string Name,
    string SourceName,
    string Status,
    DateTimeOffset UpdatedAt,
    int NodeCount,
    int RuleCount,
    bool IsActive)
{
    /// <summary>Gets the UI-filtered profile display name.</summary>
    /// <value>Display name after mainland China UI replacement; never null.</value>
    public string NameDisplay => MainlandChinaTextDisplayService.Instance.Apply(Name);

    /// <summary>Gets the UI-filtered profile source display name.</summary>
    /// <value>Source display name after mainland China UI replacement; never null.</value>
    public string SourceNameDisplay => MainlandChinaTextDisplayService.Instance.Apply(SourceName);

    /// <summary>Gets the UI-filtered profile status display text.</summary>
    /// <value>Status display text after mainland China UI replacement; never null.</value>
    public string StatusDisplay => MainlandChinaTextDisplayService.Instance.Apply(Status);
}
