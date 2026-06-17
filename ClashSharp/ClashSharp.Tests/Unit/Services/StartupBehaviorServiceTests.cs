/*
 * Startup Behavior Service Tests
 * Verifies startup behavior selection resolves to the expected proxy mode
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/StartupBehaviorServiceTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests startup behavior mode resolution.</summary>
public sealed class StartupBehaviorServiceTests
{
    /// <summary>Verifies the selected startup behavior maps to the intended runtime mode.</summary>
    [Theory]
    [InlineData(StartupBehaviorMode.LastSetting, ClashSharpMode.FullTakeover, ClashSharpMode.FullTakeover)]
    [InlineData(StartupBehaviorMode.StartRuleProxy, ClashSharpMode.FullTakeover, ClashSharpMode.RuleTakeover)]
    [InlineData(StartupBehaviorMode.DisableProxy, ClashSharpMode.FullTakeover, ClashSharpMode.Disabled)]
    public void ResolveStartupMode_MapsBehaviorToMode(
        StartupBehaviorMode behavior,
        ClashSharpMode lastMode,
        ClashSharpMode expectedMode)
    {
        ClashSharpMode mode = StartupBehaviorService.ResolveStartupMode(behavior, lastMode);

        Assert.Equal(expectedMode, mode);
    }
}
