namespace ClashSharp.ApplicationModel.Supervision;

/// <summary>Provides time and cancellable delays to supervised runtime loops.</summary>
public interface ISupervisorClock
{
    /// <summary>Gets the current UTC time.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits for the specified duration.</summary>
    /// <param name="delay">The non-negative duration to wait.</param>
    /// <param name="cancellationToken">Cancels the wait.</param>
    Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken);
}

/// <summary>Uses the system clock and task scheduler for production supervisor delays.</summary>
public sealed class SystemSupervisorClock : ISupervisorClock
{
    private SystemSupervisorClock()
    {
    }

    /// <summary>Gets the shared stateless system clock.</summary>
    public static SystemSupervisorClock Instance { get; } = new();

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;

    /// <inheritdoc />
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        return Task.Delay(delay, cancellationToken);
    }
}
