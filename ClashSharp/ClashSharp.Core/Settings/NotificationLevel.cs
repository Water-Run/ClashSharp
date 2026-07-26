namespace ClashSharp.Model;

/// <summary>Enumerates Windows system-notification verbosity policies.</summary>
public enum NotificationLevel
{
    /// <summary>Use the standard notification policy.</summary>
    Default = 0,

    /// <summary>Show only critical notifications.</summary>
    CriticalOnly = 1,

    /// <summary>Show additional informational notifications.</summary>
    More = 2,
}
