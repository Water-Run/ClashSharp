using System;

namespace ClashSharp.Service;

/// <summary>Process-wide state for settings that need restarting Clash# before taking effect.</summary>
internal sealed class RestartRequiredStateService
{
    private bool _settingsRestartPending;
    private bool _recoveryRestartPending;
    /// <summary>Singleton state source used by UI surfaces.</summary>
    public static RestartRequiredStateService Instance { get; } = new();

    /// <summary>Raised when the restart-required state changes.</summary>
    public event EventHandler? RestartPendingChanged;

    /// <summary>Gets whether any current setting requires restarting Clash#.</summary>
    public bool IsRestartPending { get; private set; }

    /// <summary>Updates restart-required state and notifies subscribers when it changes.</summary>
    public void SetRestartPending(bool isRestartPending)
    {
        _settingsRestartPending = isRestartPending;
        Publish();
    }

    /// <summary>Retains a required process restart even when a newly loaded page has no pending preferences.</summary>
    public void RequireRestart()
    {
        _recoveryRestartPending = true;
        Publish();
    }

    private void Publish()
    {
        bool isRestartPending = _settingsRestartPending || _recoveryRestartPending;
        if (IsRestartPending == isRestartPending)
        {
            return;
        }

        IsRestartPending = isRestartPending;
        RestartPendingChanged?.Invoke(this, EventArgs.Empty);
    }
}
