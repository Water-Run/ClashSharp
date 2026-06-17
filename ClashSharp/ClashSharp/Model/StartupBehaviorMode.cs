/*
 * Startup Behavior Mode Enumeration
 * Defines the proxy behavior Clash# applies during application startup
 *
 * @author: WaterRun
 * @file: Model/StartupBehaviorMode.cs
 * @date: 2026-06-17
 */

namespace ClashSharp.Model;

/// <summary>Enumerates startup proxy behavior choices.</summary>
public enum StartupBehaviorMode
{
    /// <summary>Restore the last persisted master control mode.</summary>
    LastSetting = 0,

    /// <summary>Start in rule proxy mode without forcing global takeover.</summary>
    StartRuleProxy = 1,

    /// <summary>Start with proxy takeover disabled.</summary>
    DisableProxy = 2,
}
