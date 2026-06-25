/*
 * Restart Required State Service
 * Shares restart-required settings state between settings pages and the shell
 *
 * @author: WaterRun
 * @file: Service/RestartRequiredStateService.cs
 * @date: 2026-06-25
 */

using System;

namespace ClashSharp.Service;

/// <summary>Process-wide state for settings that need restarting Clash# before taking effect.</summary>
internal sealed class RestartRequiredStateService
{
    /// <summary>Singleton state source used by UI surfaces.</summary>
    public static RestartRequiredStateService Instance { get; } = new();

    /// <summary>Raised when the restart-required state changes.</summary>
    public event EventHandler? RestartPendingChanged;

    /// <summary>Gets whether any current setting requires restarting Clash#.</summary>
    public bool IsRestartPending { get; private set; }

    /// <summary>Updates restart-required state and notifies subscribers when it changes.</summary>
    public void SetRestartPending(bool isRestartPending)
    {
        if (IsRestartPending == isRestartPending)
        {
            return;
        }

        IsRestartPending = isRestartPending;
        RestartPendingChanged?.Invoke(this, EventArgs.Empty);
    }
}
