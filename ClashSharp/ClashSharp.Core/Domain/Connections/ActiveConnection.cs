namespace ClashSharp.Model;

/// <summary>Represents one active mihomo connection row exposed by the external controller.</summary>
/// <param name="Id">Connection identifier; never null.</param>
/// <param name="ProcessName">Originating process name when reported; never null.</param>
/// <param name="Host">Destination host or address; never null.</param>
/// <param name="RuleName">Matched rule name or type; never null.</param>
/// <param name="RulePayload">Matched rule payload; never null.</param>
/// <param name="ProxyName">Selected proxy chain display text; never null.</param>
/// <param name="UploadBytes">Uploaded byte count.</param>
/// <param name="DownloadBytes">Downloaded byte count.</param>
/// <param name="StartedAt">Connection start time when reported; otherwise current time.</param>
/// <remarks>
/// Invariants: String values are never null; byte counts are non-negative.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
public readonly record struct ActiveConnection(
    string Id,
    string ProcessName,
    string Host,
    string RuleName,
    string RulePayload,
    string ProxyName,
    long UploadBytes,
    long DownloadBytes,
    DateTimeOffset StartedAt)
{
    /// <summary>Gets the raw combined rule text.</summary>
    /// <value>Rule name and payload display before UI-only filtering; never null.</value>
    public string RawRuleDisplay => string.IsNullOrWhiteSpace(RulePayload) ? RuleName : $"{RuleName},{RulePayload}";
}
