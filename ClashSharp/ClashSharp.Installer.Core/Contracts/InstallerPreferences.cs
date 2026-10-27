namespace ClashSharp.Installer.Contracts;

/// <summary>Notification presets offered during first installation.</summary>
public enum InstallerNotificationPreset
{
    /// <summary>Recommended application notifications.</summary>
    Default,
    /// <summary>Only critical notifications.</summary>
    CriticalOnly,
    /// <summary>All application notifications.</summary>
    All,
    /// <summary>No application notifications.</summary>
    Disabled,
}

/// <summary>Immutable current-user choices, independent of privileged target and release identity.</summary>
public sealed record InstallerPreferences
{
    /// <summary>Gets the recommended choices for a new installation.</summary>
    public static InstallerPreferences Default { get; } = new();
    /// <summary>Whether to create an application shortcut on the current user's desktop.</summary>
    public bool CreateDesktopShortcut { get; init; } = true;
    /// <summary>Whether the application should offer the native taskbar confirmation.</summary>
    public bool RequestTaskbarPin { get; init; }
    /// <summary>Whether to enable the application's native login startup task.</summary>
    public bool LaunchAtStartup { get; init; }
    /// <summary>Whether to register the independent login-time proxy recovery task.</summary>
    public bool RegisterProxyRecovery { get; init; } = true;
    /// <summary>Whether this installation should include the transparent proxy system service.</summary>
    public bool InstallTransparentProxyService { get; init; } = true;
    /// <summary>Whether new preferences should reflect the installing user's Windows environment.</summary>
    public bool FollowSystemDefaults { get; init; } = true;
    /// <summary>Gets the first-install notification choice.</summary>
    public InstallerNotificationPreset Notifications { get; init; } = InstallerNotificationPreset.Default;
    /// <summary>Whether to enable the local MCP endpoint with read-status permission only.</summary>
    public bool EnableMcpServer { get; init; }

    /// <summary>Checks bounded selections and native shell prerequisites.</summary>
    public void Validate()
    {
        if (!Enum.IsDefined(Notifications)) { throw new InstallerProtocolException("installer.preferences.notifications_invalid"); }
    }
}
