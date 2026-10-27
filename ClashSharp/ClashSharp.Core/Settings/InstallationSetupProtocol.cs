namespace ClashSharp.Settings;

/// <summary>Bounded current-package handoff for initial native login-task configuration.</summary>
public static class InstallationSetupProtocol
{
    /// <summary>Command accepted only with one canonical request identifier.</summary>
    public const string BootstrapArgument = "--complete-installation";
    /// <summary>Atomic application-data request value.</summary>
    public const string RequestKey = "ClashSharp.InstallSetup.Request.v1";
    /// <summary>Atomic application-data completion value.</summary>
    public const string ReceiptKey = "ClashSharp.InstallSetup.Receipt.v1";
    /// <summary>One user-requested foreground taskbar confirmation, independent of proxy permissions.</summary>
    public const string TaskbarRequestKey = "ClashSharp.InstallSetup.Taskbar.v1";
    /// <summary>Last taskbar request already offered to Windows, preventing repeated prompts.</summary>
    public const string TaskbarReceiptKey = "ClashSharp.InstallSetup.TaskbarHandled.v1";
    /// <summary>Maximum warning bits accepted in a native setup receipt.</summary>
    public const int KnownWarningBits = 3;
}
