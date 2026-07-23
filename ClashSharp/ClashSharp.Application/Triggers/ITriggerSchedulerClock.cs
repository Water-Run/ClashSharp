namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Provides deterministic periodic ticks and timestamps to the trigger scheduler.</summary>
public interface ITriggerSchedulerClock
{
    /// <summary>Gets the current UTC time used for scheduler health.</summary>
    DateTimeOffset UtcNow { get; }

    /// <summary>Waits asynchronously for the next periodic trigger tick.</summary>
    Task WaitForNextTickAsync(CancellationToken cancellationToken);
}
