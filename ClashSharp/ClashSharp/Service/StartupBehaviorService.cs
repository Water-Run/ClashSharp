/*
 * Startup Behavior Service
 * Resolves configured startup behavior into a runtime proxy mode
 *
 * @author: WaterRun
 * @file: Service/StartupBehaviorService.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Resolves startup proxy behavior settings.</summary>
internal static class StartupBehaviorService
{
    /// <summary>Maps configured startup behavior to the runtime mode to apply.</summary>
    /// <param name="behavior">Configured startup behavior.</param>
    /// <param name="lastMode">Last persisted runtime mode.</param>
    /// <returns>The runtime mode to apply.</returns>
    public static ClashSharpMode ResolveStartupMode(StartupBehaviorMode behavior, ClashSharpMode lastMode)
    {
        return behavior switch
        {
            StartupBehaviorMode.LastSetting => lastMode,
            StartupBehaviorMode.StartRuleProxy => ClashSharpMode.RuleTakeover,
            StartupBehaviorMode.DisableProxy => ClashSharpMode.Disabled,
            _ => lastMode,
        };
    }
}
