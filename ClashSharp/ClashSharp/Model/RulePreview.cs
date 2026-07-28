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
    long HitCount);
