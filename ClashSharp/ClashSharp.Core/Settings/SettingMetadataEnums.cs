namespace ClashSharp.Settings;

/// <summary>Classifies a setting by its user-facing configuration area.</summary>
public enum SettingCategory
{
    /// <summary>General application behavior.</summary>
    General = 0,

    /// <summary>Language, theme, color, or layout behavior.</summary>
    Appearance = 1,

    /// <summary>Application startup behavior.</summary>
    Startup = 2,

    /// <summary>Network takeover and proxy behavior.</summary>
    Network = 3,

    /// <summary>Connection sampling behavior.</summary>
    Sampling = 4,

    /// <summary>Trigger scheduling and execution behavior.</summary>
    Triggers = 5,

    /// <summary>Notification-area icon and menu behavior.</summary>
    Tray = 6,

    /// <summary>Regional presentation policy.</summary>
    Regional = 7,

    /// <summary>Windows notification behavior.</summary>
    Notifications = 8,
}

/// <summary>Classifies how a setting's currently effective value can be established.</summary>
public enum SettingAuthority
{
    /// <summary>The application owns the value and can verify it through its consumer contract.</summary>
    Internal = 0,

    /// <summary>The effective value must be observed from Windows, mihomo, or a hosted runtime component.</summary>
    ExternallyObserved = 1,

    /// <summary>The effective value can change only across a verified process restart.</summary>
    RestartBound = 2,
}

/// <summary>Selects the application participant responsible for applying a setting.</summary>
public enum SettingApplicationKind
{
    /// <summary>Apply by publishing a verified internal snapshot.</summary>
    Internal = 0,

    /// <summary>Apply through the appearance and localization participant.</summary>
    Appearance = 1,

    /// <summary>Apply through the network mutation participant.</summary>
    Network = 2,

    /// <summary>Apply through the Windows StartupTask participant.</summary>
    StartupTask = 3,

    /// <summary>Apply through the connection-sampling supervisor participant.</summary>
    Sampling = 4,

    /// <summary>Apply through the trigger supervisor participant.</summary>
    Triggers = 5,
}

/// <summary>Classifies when a desired setting can become effective.</summary>
public enum SettingApplicationTiming
{
    /// <summary>The setting is reconciled in the current process.</summary>
    Live = 0,

    /// <summary>The setting requires a verified process restart.</summary>
    Restart = 1,
}

/// <summary>Describes how a requested setting key resolved in the registry.</summary>
public enum SettingKeyResolution
{
    /// <summary>The key did not resolve.</summary>
    None = 0,

    /// <summary>The key is the canonical persisted key.</summary>
    Canonical = 1,

    /// <summary>The key is a read-only legacy alias.</summary>
    Alias = 2,
}
