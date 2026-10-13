using ClashSharp.Model;

namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>One coherent snapshot of the user's presentation-only format choices.</summary>
/// <param name="DataUnitStyle">Quantity scale and labels.</param>
/// <param name="RateUnit">Rate numerator.</param>
/// <param name="ClockFormat">Display clock.</param>
public sealed record DisplayFormattingOptions(
    DataUnitStyle DataUnitStyle = DataUnitStyle.BinaryWithDecimalLabels,
    RateUnit RateUnit = RateUnit.Bytes,
    ClockFormat ClockFormat = ClockFormat.FollowSystem);
