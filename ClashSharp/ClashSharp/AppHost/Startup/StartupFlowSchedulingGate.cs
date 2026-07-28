using System;
using System.Threading;
using ClashSharp.ApplicationModel.Startup;

namespace ClashSharp.Hosting.Startup;

/// <summary>Schedules post-startup UI work once, after runtime dependencies are ready.</summary>
/// <remarks>
/// A rejected or recoverably failed scheduler releases the reservation so a later readiness
/// transition can retry. A successful reservation remains terminal for the window lifetime.
/// </remarks>
internal sealed class StartupFlowSchedulingGate
{
    private int _scheduled;

    /// <summary>Attempts to schedule the startup flow after runtime readiness is established.</summary>
    internal bool TrySchedule(bool runtimeReady, Func<bool> schedule)
    {
        ArgumentNullException.ThrowIfNull(schedule);
        if (!runtimeReady || Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0)
        {
            return false;
        }

        try
        {
            if (schedule())
            {
                return true;
            }
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            // A disposed dispatcher can be retried if runtime readiness is presented again.
        }
        catch
        {
            Volatile.Write(ref _scheduled, 0);
            throw;
        }

        Volatile.Write(ref _scheduled, 0);
        return false;
    }
}
