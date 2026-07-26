namespace ClashSharp.Model;

/// <summary>Enumerates proxy behavior choices applied during startup.</summary>
public enum StartupBehaviorMode
{
    /// <summary>Restore the last persisted master-control mode.</summary>
    LastSetting = 0,

    /// <summary>Start in rule proxy mode without forcing global takeover.</summary>
    StartRuleProxy = 1,

    /// <summary>Start with proxy takeover disabled.</summary>
    DisableProxy = 2,
}
