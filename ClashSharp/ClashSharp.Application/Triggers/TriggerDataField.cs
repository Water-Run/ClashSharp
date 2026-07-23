namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Identifies one independently available trigger observation field.</summary>
public enum TriggerDataField
{
    /// <summary>Local calendar date from the injected clock.</summary>
    LocalDate = 0,

    /// <summary>Local time of day from the injected clock.</summary>
    LocalTime = 1,

    /// <summary>Traffic observed for every requested rolling duration.</summary>
    RollingTraffic = 2,

    /// <summary>Traffic observed during the current runtime session.</summary>
    CurrentSessionTraffic = 3,

    /// <summary>Persisted cumulative traffic.</summary>
    AllTimeTraffic = 4,

    /// <summary>Current upload rate.</summary>
    UploadBytesPerSecond = 5,

    /// <summary>Current download rate.</summary>
    DownloadBytesPerSecond = 6,

    /// <summary>Current active connection count.</summary>
    ActiveConnectionCount = 7,

    /// <summary>Elapsed application runtime.</summary>
    Runtime = 8,

    /// <summary>Notification severity carried by a notification event.</summary>
    NotificationLevel = 9,
}

/// <summary>Classifies why one requested trigger observation is unavailable.</summary>
public enum TriggerDataUnavailableReason
{
    /// <summary>The external source did not complete within its bounded deadline.</summary>
    Timeout = 0,

    /// <summary>The external source returned malformed or semantically invalid data.</summary>
    MalformedData = 1,

    /// <summary>The storage source was busy or locked.</summary>
    Busy = 2,

    /// <summary>The storage source failed for a reason other than contention.</summary>
    StorageFailure = 3,

    /// <summary>An expected file or stream operation failed.</summary>
    IoFailure = 4,

    /// <summary>A controller or other external source was unavailable.</summary>
    SourceUnavailable = 5,

    /// <summary>The runtime event did not carry a required observation.</summary>
    MissingEventData = 6,

    /// <summary>An unexpected provider failure was contained at the adapter boundary.</summary>
    UnexpectedFailure = 7,
}
