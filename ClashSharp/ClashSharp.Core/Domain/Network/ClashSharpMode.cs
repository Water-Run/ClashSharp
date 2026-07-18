namespace ClashSharp.Model;

/// <summary>Enumerates the primary network takeover modes exposed by ClashSharp.</summary>
public enum ClashSharpMode
{
    /// <summary>ClashSharp is not running and does not take over Windows networking.</summary>
    Disabled = 0,

    /// <summary>The core is running but defaults traffic to direct routing.</summary>
    Standby = 1,

    /// <summary>Traffic is routed through mihomo rules.</summary>
    RuleTakeover = 2,

    /// <summary>All eligible traffic is routed through the selected proxy.</summary>
    FullTakeover = 3,

    /// <summary>The desired takeover state failed and requires user-visible remediation.</summary>
    Faulted = 4,
}
