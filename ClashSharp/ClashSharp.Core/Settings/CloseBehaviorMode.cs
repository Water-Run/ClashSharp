namespace ClashSharp.Model;

/// <summary>Enumerates how the main window responds to user close requests.</summary>
public enum CloseBehaviorMode
{
    /// <summary>Exit without asking for confirmation.</summary>
    ExitWithoutConfirmation = 0,

    /// <summary>Ask for confirmation before exiting.</summary>
    ConfirmExit = 1,

    /// <summary>Hide the main window while keeping the process in the notification area.</summary>
    MinimizeToTray = 2,
}
