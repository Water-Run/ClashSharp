/*
 * Rule Preview Model
 * Represents one routing rule preview row shown by the rules page
 *
 * @author: WaterRun
 * @file: Model/RulePreview.cs
 * @date: 2026-06-15
 */

using ClashSharp.Service;

namespace ClashSharp.Model;

/// <summary>Represents one routing rule preview row shown by the rules page.</summary>
/// <param name="ProviderName">Rule provider or source name; never null.</param>
/// <param name="RuleType">Rule type such as DOMAIN-SUFFIX or GEOIP; never null.</param>
/// <param name="Payload">Rule payload display text; never null.</param>
/// <param name="Action">Resolved routing action such as DIRECT, REJECT, or PROXY; never null.</param>
/// <param name="HitCount">Observed hit count for this rule.</param>
/// <remarks>
/// Invariants: String values are never null; hit count is non-negative.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct RulePreview(
    string ProviderName,
    string RuleType,
    string Payload,
    string Action,
    long HitCount)
{
    /// <summary>Gets the UI-filtered provider display name.</summary>
    /// <value>Display name after mainland China UI replacement; never null.</value>
    public string ProviderNameDisplay => MainlandChinaTextDisplayService.Instance.Apply(ProviderName);

    /// <summary>Gets the UI-filtered payload display text.</summary>
    /// <value>Payload after mainland China UI replacement; never null.</value>
    public string PayloadDisplay => MainlandChinaTextDisplayService.Instance.Apply(Payload);
}
