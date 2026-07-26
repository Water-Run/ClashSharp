namespace ClashSharp.Settings;

/// <summary>Identifies registry-derived groups that can be reset to canonical defaults.</summary>
[Flags]
public enum SettingsResetScope
{
    /// <summary>No group-specific reset membership.</summary>
    None = 0,

    /// <summary>Basic language, appearance, and close behavior settings.</summary>
    Basic = 1 << 0,

    /// <summary>Windows notification settings.</summary>
    Notifications = 1 << 1,

    /// <summary>Application startup settings.</summary>
    Startup = 1 << 2,

    /// <summary>Trigger scheduling and trigger-notification settings.</summary>
    Triggers = 1 << 3,

    /// <summary>Notification-area icon and menu settings.</summary>
    Tray = 1 << 4,

    /// <summary>Transparent proxy settings.</summary>
    TransparentProxy = 1 << 5,

    /// <summary>Proxy runtime and connection sampling settings.</summary>
    Proxy = 1 << 6,

    /// <summary>Connection-test target settings.</summary>
    ConnectionTests = 1 << 7,

    /// <summary>Windows proxy recovery policy settings.</summary>
    WindowsNative = 1 << 8,

    /// <summary>Mainland China presentation-policy settings.</summary>
    MainlandChina = 1 << 9,

    /// <summary>Master-control presentation settings.</summary>
    MasterControl = 1 << 10,

    /// <summary>Every canonical setting, including settings without a group membership.</summary>
    All = 1 << 30,
}
