namespace ClashSharp.ApplicationModel.Supervision;

/// <summary>Calculates bounded supervisor retry delays with an injectable jitter source.</summary>
public sealed class SupervisorBackoffPolicy
{
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    private readonly Func<double> _jitterSource;

    /// <summary>Initializes a retry policy.</summary>
    /// <param name="jitterSource">
    /// Supplies a value from minus one through one, scaled to plus-or-minus ten percent.
    /// Omit it for exact, non-jittered delays.
    /// </param>
    public SupervisorBackoffPolicy(Func<double>? jitterSource = null)
    {
        _jitterSource = jitterSource ?? (() => 0d);
    }

    /// <summary>Creates the deterministic-per-service production policy.</summary>
    /// <param name="serviceName">Stable service name used as the jitter seed.</param>
    /// <returns>A policy whose delay remains stable for the supplied name.</returns>
    public static SupervisorBackoffPolicy CreateProduction(string serviceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);
        double jitter = CalculateDeterministicJitter(serviceName);
        return new SupervisorBackoffPolicy(() => jitter);
    }

    /// <summary>Gets the delay after a failed iteration.</summary>
    /// <param name="consecutiveFailureCount">One-based consecutive failure count.</param>
    /// <param name="recoveryRelapse">Whether this failure interrupted recovery and must use the capped delay.</param>
    /// <returns>The bounded delay with configured jitter.</returns>
    public TimeSpan GetDelay(int consecutiveFailureCount, bool recoveryRelapse = false)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(consecutiveFailureCount, 1);

        TimeSpan baseline = recoveryRelapse
            ? RetryDelays[^1]
            : RetryDelays[Math.Min(consecutiveFailureCount - 1, RetryDelays.Length - 1)];
        double sample;
        try
        {
            sample = _jitterSource();
        }
        catch
        {
            sample = 0d;
        }

        if (!double.IsFinite(sample))
        {
            sample = 0d;
        }

        double boundedSample = Math.Clamp(sample, -1d, 1d);
        return TimeSpan.FromTicks((long)Math.Round(
            baseline.Ticks * (1d + (boundedSample * 0.1d)),
            MidpointRounding.AwayFromZero));
    }

    private static double CalculateDeterministicJitter(string serviceName)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        foreach (char character in serviceName)
        {
            hash ^= char.ToLowerInvariant(character);
            hash *= prime;
        }

        return ((hash / (double)uint.MaxValue) * 2d) - 1d;
    }
}
