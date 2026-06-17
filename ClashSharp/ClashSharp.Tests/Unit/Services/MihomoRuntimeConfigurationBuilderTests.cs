/*
 * Mihomo Runtime Configuration Builder Tests
 * Verifies deterministic mihomo configuration generation and runtime key overlay behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MihomoRuntimeConfigurationBuilderTests.cs
 * @date: 2026-06-15
 */

using System;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests pure mihomo runtime configuration generation behavior.</summary>
/// <remarks>
/// Invariants: Tests must not start mihomo, mutate Windows proxy settings, or touch user application data.
/// Thread safety: xUnit may run tests concurrently; tested methods are stateless.
/// Side effects: None.
/// </remarks>
public sealed class MihomoRuntimeConfigurationBuilderTests
{
    /// <summary>Verifies default configuration includes direct routing and the requested mixed port.</summary>
    [Fact]
    public void BuildDefaultConfiguration_StandbyWithoutTun_EmitsDirectModeAndPort()
    {
        string configuration = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
            7890,
            ClashSharpMode.Standby,
            transparentProxyEnabled: false);

        Assert.Contains("mixed-port: 7890", configuration, StringComparison.Ordinal);
        Assert.Contains("mode: direct", configuration, StringComparison.Ordinal);
        Assert.Contains("rules:\n  - MATCH,DIRECT", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("tun:\n", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies default configuration includes Clash# controlled TUN settings when requested.</summary>
    [Fact]
    public void BuildDefaultConfiguration_RuleTakeoverWithTun_EmitsTunSection()
    {
        string configuration = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
            7891,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true);

        Assert.Contains("mixed-port: 7891", configuration, StringComparison.Ordinal);
        Assert.Contains("mode: rule", configuration, StringComparison.Ordinal);
        Assert.Contains("tun:\n  enable: true", configuration, StringComparison.Ordinal);
        Assert.Contains("  auto-route: true", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies imported runtime keys are replaced while unrelated profile content remains.</summary>
    [Fact]
    public void OverrideRuntimeKeys_ExistingKeys_ReplacesControlledValues()
    {
        const string ImportedConfiguration = """
            mixed-port: 1080
            mode: direct
            tun:
              enable: false
              stack: gvisor
            proxies:
              - name: US Node
                type: ss
                server: example.invalid
                port: 443
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - US Node
            rules:
              - MATCH,GLOBAL
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7892,
            ClashSharpMode.FullTakeover,
            transparentProxyEnabled: true);

        Assert.Contains("mixed-port: 7892", configuration, StringComparison.Ordinal);
        Assert.Contains("mode: global", configuration, StringComparison.Ordinal);
        Assert.Contains("tun:\n  enable: true", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("stack: gvisor", configuration, StringComparison.Ordinal);
        Assert.Contains("name: US Node", configuration, StringComparison.Ordinal);
        Assert.Contains("- MATCH,GLOBAL", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies missing controlled keys are inserted deterministically.</summary>
    [Fact]
    public void OverrideRuntimeKeys_MissingKeys_InsertsModeAndPort()
    {
        const string ImportedConfiguration = """
            proxies: []
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            rules:
              - MATCH,DIRECT
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7893,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false);

        Assert.StartsWith("mode: rule\nmixed-port: 7893\n", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("tun:\n", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies TUN enablement follows the selected master status and user preference.</summary>
    [Theory]
    [InlineData(ClashSharpMode.Disabled, true, false)]
    [InlineData(ClashSharpMode.Standby, true, false)]
    [InlineData(ClashSharpMode.RuleTakeover, true, true)]
    [InlineData(ClashSharpMode.FullTakeover, true, true)]
    [InlineData(ClashSharpMode.RuleTakeover, false, false)]
    public void ShouldEnableTransparentProxy_UsesActiveTakeoverModesOnly(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        bool expected)
    {
        bool actual = MihomoRuntimeConfigurationBuilder.ShouldEnableTransparentProxy(mode, transparentProxyEnabled);

        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies invalid ports are rejected before configuration text is emitted.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void BuildDefaultConfiguration_InvalidPort_Throws(int mixedPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
                mixedPort,
                ClashSharpMode.Standby,
                transparentProxyEnabled: false));
    }
}
