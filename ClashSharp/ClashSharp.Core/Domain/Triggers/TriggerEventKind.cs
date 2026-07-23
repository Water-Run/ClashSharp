namespace ClashSharp.Model.Triggers;

/// <summary>Identifies the event that requested trigger evaluation.</summary>
public enum TriggerEventKind
{
    /// <summary>A scheduler tick requested evaluation.</summary>
    Periodic = 0,

    /// <summary>The application entered its active runtime.</summary>
    AppEntered = 1,

    /// <summary>The owned proxy runtime started.</summary>
    ProxyStarted = 2,

    /// <summary>A notification was raised.</summary>
    NotificationRaised = 3,
}

/// <summary>Identifies the typed parameter shape of a trigger condition.</summary>
public enum TriggerConditionKind
{
    /// <summary>An application or proxy runtime event.</summary>
    Event = 0,

    /// <summary>A notification event filtered by severity.</summary>
    Notification = 1,

    /// <summary>A traffic-byte threshold with an explicit scope.</summary>
    Traffic = 2,

    /// <summary>An upload or download rate threshold.</summary>
    Rate = 3,

    /// <summary>An active-connection-count threshold.</summary>
    ActiveConnections = 4,

    /// <summary>An application-runtime duration threshold.</summary>
    Runtime = 5,

    /// <summary>A local time-of-day schedule.</summary>
    SystemTime = 6,
}

/// <summary>Identifies which traffic history a byte threshold observes.</summary>
public enum TriggerTrafficScope
{
    /// <summary>Traffic observed during a configured rolling duration.</summary>
    RollingWindow = 0,

    /// <summary>Traffic observed during the current process session.</summary>
    CurrentSession = 1,

    /// <summary>Persisted cumulative traffic across all sessions.</summary>
    AllTime = 2,
}

/// <summary>Identifies one traffic-rate direction.</summary>
public enum TriggerTrafficDirection
{
    /// <summary>Uploaded bytes per second.</summary>
    Upload = 0,

    /// <summary>Downloaded bytes per second.</summary>
    Download = 1,
}

/// <summary>Identifies the minimum notification severity matched by a trigger.</summary>
public enum TriggerNotificationLevel
{
    /// <summary>Normal application notifications.</summary>
    Default = 0,

    /// <summary>Only critical notifications.</summary>
    CriticalOnly = 1,

    /// <summary>Verbose notifications.</summary>
    More = 2,
}

/// <summary>Identifies a supported trigger action effect.</summary>
public enum TriggerActionKind
{
    /// <summary>Closes all active proxy connections.</summary>
    CloseConnections = 0,

    /// <summary>Sets launch-at-startup state.</summary>
    SetLaunchAtStartup = 1,

    /// <summary>Sets transparent-proxy preference.</summary>
    SetTransparentProxy = 2,

    /// <summary>Sets connection-sampling state.</summary>
    SetConnectionSampling = 3,

    /// <summary>Applies a primary proxy mode.</summary>
    SwitchProxyMode = 4,

    /// <summary>Hands application exit to the outer process lifetime.</summary>
    ExitApplication = 5,

    /// <summary>Sends one deduplicated notification.</summary>
    SendNotification = 6,
}
