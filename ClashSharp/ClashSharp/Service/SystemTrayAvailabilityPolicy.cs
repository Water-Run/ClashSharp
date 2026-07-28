using System;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Service;

/// <summary>Revalidates notification-area availability and recovers an icon lost with Explorer.</summary>
internal static class SystemTrayAvailabilityPolicy
{
    /// <summary>Registers the Explorer-restart message and rejects the Win32 failure sentinel.</summary>
    internal static bool TryRegisterRecoveryMessage(
        Func<uint> registerMessage,
        out uint message)
    {
        ArgumentNullException.ThrowIfNull(registerMessage);

        try
        {
            message = registerMessage();
            return message != 0;
        }
        catch (Exception exception) when (StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            message = 0;
            return false;
        }
    }

    /// <summary>Attempts a modify first, then an add when the shell no longer recognizes the icon.</summary>
    internal static bool TryEnsureAvailable(
        Func<bool> modify,
        Func<bool> add)
    {
        ArgumentNullException.ThrowIfNull(modify);
        ArgumentNullException.ThrowIfNull(add);

        return TryNativeOperation(modify) || TryNativeOperation(add);
    }

    /// <summary>
    /// Confirms both icon availability and an Explorer-restart recovery message before the only
    /// application window may be hidden.
    /// </summary>
    internal static bool CanHideToTray(
        uint recoveryMessage,
        Func<bool> ensureAvailable)
    {
        ArgumentNullException.ThrowIfNull(ensureAvailable);
        return recoveryMessage != 0 && TryNativeOperation(ensureAvailable);
    }

    /// <summary>
    /// Refreshes or re-establishes the tray entry and restores a previously hidden window whenever
    /// notification-area reachability cannot be confirmed.
    /// </summary>
    internal static bool TryRefreshAndPreserveReachability(
        Func<bool> ensureAvailable,
        bool wasWindowHiddenToTray,
        Action restoreWindow)
    {
        ArgumentNullException.ThrowIfNull(ensureAvailable);
        ArgumentNullException.ThrowIfNull(restoreWindow);

        bool available = TryNativeOperation(ensureAvailable);
        if (!available && wasWindowHiddenToTray)
        {
            restoreWindow();
        }

        return available;
    }

    private static bool TryNativeOperation(Func<bool> operation)
    {
        try
        {
            return operation();
        }
        catch (Exception exception) when (StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            return false;
        }
    }
}
