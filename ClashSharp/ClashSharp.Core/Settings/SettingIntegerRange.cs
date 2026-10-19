namespace ClashSharp.Settings;

/// <summary>Describes the inclusive bounds used to validate an integer setting.</summary>
public sealed record SettingIntegerRange
{
    /// <summary>Initializes validated inclusive integer bounds.</summary>
    /// <param name="minimum">Inclusive minimum value.</param>
    /// <param name="maximum">Inclusive maximum value, which cannot be less than the minimum.</param>
    public SettingIntegerRange(int minimum, int maximum)
    {
        if (minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), "Minimum cannot exceed maximum.");
        }

        Minimum = minimum;
        Maximum = maximum;
    }

    /// <summary>Gets the inclusive minimum value.</summary>
    public int Minimum { get; }

    /// <summary>Gets the inclusive maximum value.</summary>
    public int Maximum { get; }
}
