using System;

namespace ClashSharp.ViewModel;

/// <summary>Restart-required state contract used by the shell navigation label.</summary>
internal interface IShellRestartState
{
    /// <summary>Occurs when restart-required state changes.</summary>
    event EventHandler? RestartPendingChanged;

    /// <summary>Gets whether any current setting requires restarting Clash# to apply.</summary>
    bool IsRestartPending { get; }
}
