using System.Collections.ObjectModel;

namespace ClashSharp.Model.Triggers;

/// <summary>Immutable point-in-time data supplied to the pure trigger matcher.</summary>
/// <remarks>
/// Null optional values are unavailable, not zero. Rolling traffic contains only durations that were observed.
/// Thread safety: Immutable after construction.
/// Side effects: None.
/// </remarks>
public sealed class TriggerEvaluationContext
{
    /// <summary>Initializes one immutable evaluation context.</summary>
    /// <param name="eventKind">Event requesting evaluation.</param>
    /// <param name="localDate">Explicit local calendar date.</param>
    /// <param name="localTime">Explicit local time of day.</param>
    /// <param name="rollingTrafficBytes">Observed traffic totals keyed by exact rolling duration.</param>
    /// <param name="currentSessionTrafficBytes">Current-process-session traffic, or null when unavailable.</param>
    /// <param name="allTimeTrafficBytes">Persisted cumulative traffic, or null when unavailable.</param>
    /// <param name="uploadBytesPerSecond">Current upload rate, or null when unavailable.</param>
    /// <param name="downloadBytesPerSecond">Current download rate, or null when unavailable.</param>
    /// <param name="activeConnectionCount">Active connection count, or null when unavailable.</param>
    /// <param name="runtime">Current application runtime, or null when unavailable.</param>
    /// <param name="notificationLevel">Notification severity for a notification event, or null when unavailable.</param>
    public TriggerEvaluationContext(
        TriggerEventKind eventKind,
        DateOnly localDate,
        TimeOnly localTime,
        IReadOnlyDictionary<TimeSpan, long>? rollingTrafficBytes = null,
        long? currentSessionTrafficBytes = null,
        long? allTimeTrafficBytes = null,
        long? uploadBytesPerSecond = null,
        long? downloadBytesPerSecond = null,
        int? activeConnectionCount = null,
        TimeSpan? runtime = null,
        TriggerNotificationLevel? notificationLevel = null)
    {
        if (!Enum.IsDefined(eventKind))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (notificationLevel is TriggerNotificationLevel suppliedLevel && !Enum.IsDefined(suppliedLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(notificationLevel));
        }

        ValidateOptionalNonnegative(currentSessionTrafficBytes, nameof(currentSessionTrafficBytes));
        ValidateOptionalNonnegative(allTimeTrafficBytes, nameof(allTimeTrafficBytes));
        ValidateOptionalNonnegative(uploadBytesPerSecond, nameof(uploadBytesPerSecond));
        ValidateOptionalNonnegative(downloadBytesPerSecond, nameof(downloadBytesPerSecond));
        ValidateOptionalNonnegative(activeConnectionCount, nameof(activeConnectionCount));
        if (runtime is TimeSpan suppliedRuntime && suppliedRuntime < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(runtime));
        }

        if (rollingTrafficBytes is not null
            && rollingTrafficBytes.Any(static pair => pair.Key <= TimeSpan.Zero || pair.Value < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(rollingTrafficBytes));
        }

        EventKind = eventKind;
        LocalDate = localDate;
        LocalTime = localTime;
        RollingTrafficBytes = new ReadOnlyDictionary<TimeSpan, long>(
            rollingTrafficBytes is null
                ? new Dictionary<TimeSpan, long>()
                : new Dictionary<TimeSpan, long>(rollingTrafficBytes));
        CurrentSessionTrafficBytes = currentSessionTrafficBytes;
        AllTimeTrafficBytes = allTimeTrafficBytes;
        UploadBytesPerSecond = uploadBytesPerSecond;
        DownloadBytesPerSecond = downloadBytesPerSecond;
        ActiveConnectionCount = activeConnectionCount;
        Runtime = runtime;
        NotificationLevel = notificationLevel;
    }

    private static void ValidateOptionalNonnegative<T>(T? value, string parameterName)
        where T : struct, IComparable<T>
    {
        if (value is T supplied && supplied.CompareTo(default) < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName);
        }
    }

    /// <summary>Gets the event requesting evaluation.</summary>
    public TriggerEventKind EventKind { get; }

    /// <summary>Gets the explicit local calendar date.</summary>
    public DateOnly LocalDate { get; }

    /// <summary>Gets the explicit local time of day.</summary>
    public TimeOnly LocalTime { get; }

    /// <summary>Gets observed traffic totals keyed by exact rolling duration.</summary>
    public ReadOnlyDictionary<TimeSpan, long> RollingTrafficBytes { get; }

    /// <summary>Gets current-process-session traffic, or null when unavailable.</summary>
    public long? CurrentSessionTrafficBytes { get; }

    /// <summary>Gets persisted cumulative traffic, or null when unavailable.</summary>
    public long? AllTimeTrafficBytes { get; }

    /// <summary>Gets current upload rate, or null when unavailable.</summary>
    public long? UploadBytesPerSecond { get; }

    /// <summary>Gets current download rate, or null when unavailable.</summary>
    public long? DownloadBytesPerSecond { get; }

    /// <summary>Gets active connection count, or null when unavailable.</summary>
    public int? ActiveConnectionCount { get; }

    /// <summary>Gets current application runtime, or null when unavailable.</summary>
    public TimeSpan? Runtime { get; }

    /// <summary>Gets notification severity for a notification event, or null when unavailable.</summary>
    public TriggerNotificationLevel? NotificationLevel { get; }
}
