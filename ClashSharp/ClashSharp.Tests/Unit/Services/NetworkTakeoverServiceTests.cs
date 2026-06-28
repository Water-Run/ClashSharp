/*
 * Network Takeover Service Tests
 * Verifies proxy takeover mode coordination through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/NetworkTakeoverServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for network takeover mode application.</summary>
public sealed class NetworkTakeoverServiceTests
{
    /// <summary>Verifies an installed transparent proxy service keeps Windows proxy disabled and starts TUN mode.</summary>
    [Fact]
    public void ApplyMode_WhenTransparentProxyEnabledAndServiceInstalled_UsesTransparentProxy()
    {
        FakeNetworkTakeoverSettings settings = new()
        {
            TransparentProxyEnabled = true,
            MixedPort = 19090,
        };
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, true, "Installed"));
        NetworkTakeoverService service = CreateService(
            settings,
            configuration,
            core,
            windowsProxy,
            serviceStatus);

        NetworkTakeoverResult result = service.ApplyMode(ClashSharpMode.FullTakeover);

        Assert.Equal(ClashSharpMode.FullTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.False(result.SystemProxyEnabled);
        Assert.True(result.TransparentProxyEnabled);
        Assert.Equal("transparent full", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.FullTakeover, true)], configuration.Requests);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.False(core.Stopped);
        Assert.Equal(1, windowsProxy.DisableCount);
        Assert.Empty(windowsProxy.EnabledServers);
    }

    /// <summary>Verifies a stopped transparent proxy service falls back to system proxy.</summary>
    [Fact]
    public void ApplyMode_WhenTransparentProxyServiceInstalledButStopped_FallsBackToSystemProxy()
    {
        FakeNetworkTakeoverSettings settings = new()
        {
            TransparentProxyEnabled = true,
            MixedPort = 10002,
        };
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, false, "Stopped"));
        NetworkTakeoverService service = CreateService(
            settings,
            configuration,
            core,
            windowsProxy,
            serviceStatus);

        NetworkTakeoverResult result = service.ApplyMode(ClashSharpMode.FullTakeover);

        Assert.Equal(ClashSharpMode.FullTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal("missing full", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.FullTakeover, false)], configuration.Requests);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.Equal(["127.0.0.1:10002"], windowsProxy.EnabledServers);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    /// <summary>Verifies missing transparent proxy service falls back to system proxy without clearing the preference.</summary>
    [Fact]
    public void ApplyMode_WhenTransparentProxyEnabledButServiceMissing_FallsBackToSystemProxyWithoutClearingPreference()
    {
        FakeNetworkTakeoverSettings settings = new()
        {
            TransparentProxyEnabled = true,
            MixedPort = 10001,
        };
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(false, false, "Missing"));
        NetworkTakeoverService service = CreateService(
            settings,
            configuration,
            core,
            windowsProxy,
            serviceStatus);

        NetworkTakeoverResult result = service.ApplyMode(ClashSharpMode.RuleTakeover);

        Assert.Equal(ClashSharpMode.RuleTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.True(settings.TransparentProxyEnabled);
        Assert.Equal("missing rule", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.RuleTakeover, false)], configuration.Requests);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.Equal(["127.0.0.1:10001"], windowsProxy.EnabledServers);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    /// <summary>Verifies disabled mode stops mihomo and disables Windows system proxy through dependencies.</summary>
    [Fact]
    public void ApplyMode_WhenDisabled_StopsCoreAndDisablesSystemProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        NetworkTakeoverService service = CreateService(configuration: configuration, core: core, windowsProxy: windowsProxy);

        NetworkTakeoverResult result = service.ApplyMode(ClashSharpMode.Disabled);

        Assert.Equal(ClashSharpMode.Disabled, result.Mode);
        Assert.False(result.CoreRunning);
        Assert.False(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal("disabled", result.Message);
        Assert.True(core.Stopped);
        Assert.Empty(core.RestartedStates);
        Assert.Empty(configuration.Requests);
        Assert.Equal(1, windowsProxy.DisableCount);
        Assert.Empty(windowsProxy.EnabledServers);
    }

    private static NetworkTakeoverService CreateService(
        FakeNetworkTakeoverSettings? settings = null,
        FakeNetworkTakeoverCoreConfiguration? configuration = null,
        FakeNetworkTakeoverCore? core = null,
        FakeNetworkTakeoverWindowsProxy? windowsProxy = null,
        FakeNetworkTakeoverMihomoService? serviceStatus = null,
        FakeNetworkTakeoverProxyRecovery? proxyRecovery = null)
    {
        return new NetworkTakeoverService(
            settings ?? new FakeNetworkTakeoverSettings(),
            configuration ?? new FakeNetworkTakeoverCoreConfiguration(),
            core ?? new FakeNetworkTakeoverCore(),
            windowsProxy ?? new FakeNetworkTakeoverWindowsProxy(),
            serviceStatus ?? new FakeNetworkTakeoverMihomoService(new MihomoServiceStatus(true, true, "Installed")),
            proxyRecovery ?? new FakeNetworkTakeoverProxyRecovery(),
            key => key switch
            {
                "NetworkTakeover.Disabled" => "disabled",
                "NetworkTakeover.Standby" => "standby",
                "NetworkTakeover.StartupRecovered" => "startup recovered",
                "NetworkTakeover.SystemProxy.Full" => "system full",
                "NetworkTakeover.SystemProxy.Rule" => "system rule",
                "NetworkTakeover.TransparentProxy.Full" => "transparent full",
                "NetworkTakeover.TransparentProxy.Rule" => "transparent rule",
                "NetworkTakeover.TransparentProxyServiceMissing.Full" => "missing full",
                "NetworkTakeover.TransparentProxyServiceMissing.Rule" => "missing rule",
                _ => key,
            });
    }

    private sealed class FakeNetworkTakeoverSettings : INetworkTakeoverSettings
    {
        public bool TransparentProxyEnabled { get; set; }

        public int MixedPort { get; set; } = 7890;
    }

    private sealed class FakeNetworkTakeoverCoreConfiguration : INetworkTakeoverCoreConfiguration
    {
        public CoreConfigurationState State { get; } = new(@"C:\mihomo", @"C:\mihomo\config.yaml", true);

        public List<ConfigurationRequest> Requests { get; } = [];

        public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool transparentProxyEnabled)
        {
            Requests.Add(new ConfigurationRequest(mode, transparentProxyEnabled));
            return State;
        }
    }

    private sealed class FakeNetworkTakeoverCore : INetworkTakeoverCore
    {
        public bool Stopped { get; private set; }

        public List<CoreConfigurationState> RestartedStates { get; } = [];

        public void Stop()
        {
            Stopped = true;
        }

        public void Restart(CoreConfigurationState configurationState)
        {
            RestartedStates.Add(configurationState);
        }
    }

    private sealed class FakeNetworkTakeoverWindowsProxy : INetworkTakeoverWindowsProxy
    {
        public int DisableCount { get; private set; }

        public List<string> EnabledServers { get; } = [];

        public void DisableProxy()
        {
            DisableCount++;
        }

        public void EnableProxy(string proxyServer)
        {
            EnabledServers.Add(proxyServer);
        }
    }

    private sealed class FakeNetworkTakeoverMihomoService(MihomoServiceStatus status) : INetworkTakeoverMihomoService
    {
        public MihomoServiceStatus GetStatus()
        {
            return status;
        }
    }

    private sealed class FakeNetworkTakeoverProxyRecovery : INetworkTakeoverProxyRecovery
    {
        public string BuildLoopbackProxyServer(int mixedPort)
        {
            return $"127.0.0.1:{mixedPort}";
        }
    }

    private readonly record struct ConfigurationRequest(ClashSharpMode Mode, bool TransparentProxyEnabled);
}
