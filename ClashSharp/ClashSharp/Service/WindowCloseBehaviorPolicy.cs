using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Describes the safe action for a user-initiated main-window close request.</summary>
internal enum WindowCloseDisposition
{
    /// <summary>Hide the window because a confirmed notification-area entry can restore it.</summary>
    HideToTray,

    /// <summary>Hand the request to the application-owned safe shutdown path.</summary>
    RequestSafeExit,

    /// <summary>Ask the user to confirm application exit.</summary>
    ConfirmExit,
}

/// <summary>Prevents close behavior from hiding the only reachable application window.</summary>
internal static class WindowCloseBehaviorPolicy
{
    /// <summary>Resolves close behavior against the currently available tray capability.</summary>
    internal static WindowCloseDisposition Resolve(
        CloseBehaviorMode closeBehavior,
        bool isTrayAvailable)
    {
        return closeBehavior switch
        {
            CloseBehaviorMode.MinimizeToTray when isTrayAvailable =>
                WindowCloseDisposition.HideToTray,
            CloseBehaviorMode.MinimizeToTray or CloseBehaviorMode.ExitWithoutConfirmation =>
                WindowCloseDisposition.RequestSafeExit,
            _ => WindowCloseDisposition.ConfirmExit,
        };
    }
}
