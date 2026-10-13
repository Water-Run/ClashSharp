namespace ClashSharp.Model;

/// <summary>Controls the display clock independently of interface language.</summary>
public enum ClockFormat
{
    /// <summary>Preserves the user's Windows time pattern and regional overrides.</summary>
    FollowSystem,
    /// <summary>Uses a twelve-hour clock with the regional AM/PM designator.</summary>
    TwelveHour,
    /// <summary>Uses a twenty-four-hour clock.</summary>
    TwentyFourHour,
}
