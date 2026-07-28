using System;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Service;

/// <summary>Completes tray registration while preserving ownership on partially initialized failures.</summary>
internal static class SystemTrayInitializationPolicy
{
    /// <summary>Runs tray registration and refresh, releasing acquired resources when either step fails.</summary>
    /// <param name="resource">Resource acquired before native registration.</param>
    /// <param name="add">Native tray registration operation.</param>
    /// <param name="refresh">Initial state and icon refresh operation.</param>
    /// <param name="remove">Idempotent native tray removal operation.</param>
    internal static void Complete(
        IDisposable resource,
        Func<bool> add,
        Func<bool> refresh,
        Action remove)
    {
        ArgumentNullException.ThrowIfNull(resource);
        ArgumentNullException.ThrowIfNull(add);
        ArgumentNullException.ThrowIfNull(refresh);
        ArgumentNullException.ThrowIfNull(remove);

        try
        {
            if (!add() || !refresh())
            {
                throw new InvalidOperationException(
                    "The notification-area icon could not be registered.");
            }
        }
        catch
        {
            TryCleanup(remove);
            TryCleanup(resource.Dispose);
            throw;
        }
    }

    private static void TryCleanup(Action cleanup)
    {
        try
        {
            cleanup();
        }
        catch (Exception exception) when (StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            // Preserve the original initialization failure while releasing everything still reachable.
        }
    }
}
