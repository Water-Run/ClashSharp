using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>
/// Decides what a verified proxy mode means for the user: a notification only when the mode actually changed, and
/// a proxy-start event only when traffic takeover begins.
/// </summary>
/// <remarks>
/// Every way of applying a mode (the Master page, the tray menu, a trigger action, the startup behavior) reports the
/// verified result here. Choosing the mode that is already active re-applies it, which repairs a drifted runtime,
/// but it is not a change: announcing it again would tell the user something false and would fire "when the proxy
/// starts" triggers for a proxy that was already running. Until the first report nothing runs under this process,
/// so the first takeover is announced as a start.
/// </remarks>
internal sealed class ProxyModeAnnouncer
{
    private readonly object _gate = new();
    private ClashSharpMode? _announced;

    /// <summary>Records one verified mode and returns what it means.</summary>
    /// <param name="mode">The verified mode.</param>
    /// <returns>Whether the mode changed, and whether traffic takeover started with it.</returns>
    internal (bool ModeChanged, bool ProxyStarted) Observe(ClashSharpMode mode)
    {
        lock (_gate)
        {
            ClashSharpMode previous = _announced ?? ClashSharpMode.Disabled;
            bool changed = _announced is null ? mode != ClashSharpMode.Disabled : mode != previous;
            _announced = mode;
            return (changed, changed && IsTakeover(mode) && !IsTakeover(previous));
        }
    }

    private static bool IsTakeover(ClashSharpMode mode) => mode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover;
}
