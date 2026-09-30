using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Immutable, localized traffic row prepared before a statistics snapshot is published.</summary>
/// <param name="Model">The original storage row, with its identity and counters preserved.</param>
/// <param name="Label">Presentation label, after profile lookup or display filtering.</param>
/// <param name="TotalDisplay">Total traffic with its localized label.</param>
/// <param name="TransferDisplay">Upload and download with their localized labels.</param>
/// <param name="SampleCountDisplay">Source count with the correct connection or snapshot label.</param>
/// <param name="UpdatedAtDisplay">Localized last-update text.</param>
internal sealed record StatisticsTrafficRowDisplay(
    TrafficStatisticRow Model,
    string Label,
    string TotalDisplay,
    string TransferDisplay,
    string SampleCountDisplay,
    string UpdatedAtDisplay);

/// <summary>Immutable presentation row for a persisted rule-hit aggregate.</summary>
/// <param name="Label">Rule label after the UI display policy is applied.</param>
/// <param name="HitCount">Persisted hit count, including zero.</param>
/// <param name="HitCountDisplay">Localized hit count text.</param>
internal sealed record StatisticsRuleHitDisplay(string Label, long HitCount, string HitCountDisplay);
