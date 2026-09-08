namespace ClashSharp.ApplicationModel.Settings;

/// <summary>One validated, immutable sampling preference pair.</summary>
public sealed record ConnectionSamplingSettings
{
    /// <summary>Creates a complete sampling preference pair.</summary>
    /// <param name="enabled">Whether the sampling loop should run.</param>
    /// <param name="intervalSeconds">Sampling interval in the inclusive range 3 to 300 seconds.</param>
    public ConnectionSamplingSettings(bool enabled, int intervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(intervalSeconds, 3);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(intervalSeconds, 300);
        Enabled = enabled;
        IntervalSeconds = intervalSeconds;
    }

    /// <summary>Gets the desired loop activation state.</summary>
    public bool Enabled { get; }

    /// <summary>Gets the validated sampling interval.</summary>
    public int IntervalSeconds { get; }
}
