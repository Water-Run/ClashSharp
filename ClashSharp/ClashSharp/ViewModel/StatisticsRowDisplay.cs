using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Immutable, localized traffic row prepared before a statistics snapshot is published.</summary>
/// <param name="Model">The original storage row, with its identity and counters preserved.</param>
/// <param name="Label">Presentation label, after profile lookup or display filtering.</param>
/// <param name="TotalDisplay">Total traffic with its localized label.</param>
/// <param name="TransferDisplay">Upload and download with their localized labels.</param>
/// <param name="SampleCountDisplay">Source count with the correct connection or snapshot label.</param>
/// <param name="UpdatedAtDisplay">Localized last-update text.</param>
/// <param name="Share">This row's total relative to the largest total in the same breakdown, from 0 to 1.</param>
internal sealed record StatisticsTrafficRowDisplay(
    TrafficStatisticRow Model,
    string Label,
    string TotalDisplay,
    string TransferDisplay,
    string SampleCountDisplay,
    string UpdatedAtDisplay,
    double Share = 0)
{
    /// <summary>Gets <see cref="Share"/> on the 0 to 100 scale used by native range controls.</summary>
    public double SharePercent => Share * 100;
}

/// <summary>Immutable presentation row for a persisted rule-hit aggregate.</summary>
/// <param name="Label">Rule label after the UI display policy is applied.</param>
/// <param name="HitCount">Persisted hit count, including zero.</param>
/// <param name="HitCountDisplay">Localized hit count text.</param>
/// <param name="Share">This rule's hits relative to the most-hit rule, from 0 to 1.</param>
internal sealed record StatisticsRuleHitDisplay(string Label, long HitCount, string HitCountDisplay, double Share = 0)
{
    /// <summary>Gets <see cref="Share"/> on the 0 to 100 scale used by native range controls.</summary>
    public double SharePercent => Share * 100;
}

/// <summary>One calendar day in the recent-traffic column chart, including days without samples.</summary>
/// <param name="Date">Local calendar day represented by the column.</param>
/// <param name="DayLabel">Compact axis label, normally the day of month.</param>
/// <param name="DateLabel">Localized full date shown in the tooltip.</param>
/// <param name="TotalDisplay">Localized total traffic for the day.</param>
/// <param name="TransferDisplay">Localized upload and download for the day.</param>
/// <param name="Fraction">Column height relative to the busiest day in the window, from 0 to 1.</param>
/// <param name="AccessibleText">Complete spoken description so the value never depends on bar height alone.</param>
internal sealed record StatisticsDailyBarDisplay(
    System.DateOnly Date,
    string DayLabel,
    string DateLabel,
    string TotalDisplay,
    string TransferDisplay,
    double Fraction,
    string AccessibleText);
