using System;

namespace ClashSharp.ViewModel;

/// <summary>Empty restart state source used by tests and unsupported shells.</summary>
internal sealed class NoShellRestartState : IShellRestartState
{
    public static NoShellRestartState Instance { get; } = new();

    public event EventHandler? RestartPendingChanged
    {
        add { }
        remove { }
    }

    public bool IsRestartPending => false;
}
