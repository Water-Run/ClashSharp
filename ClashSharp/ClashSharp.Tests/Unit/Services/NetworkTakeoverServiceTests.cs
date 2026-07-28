using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for network takeover mode application.</summary>
public sealed class NetworkTakeoverServiceTests
{
    /// <summary>Verifies an installed transparent proxy service keeps Windows proxy disabled and starts TUN mode.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTransparentProxyEnabledAndServiceInstalled_UsesTransparentProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, true, "Installed"));
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.FullTakeover,
            true,
            19090,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.FullTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.False(result.SystemProxyEnabled);
        Assert.True(result.TransparentProxyEnabled);
        Assert.Equal("transparent full", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.FullTakeover, true, 19090)], configuration.Requests);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.False(core.Stopped);
        Assert.Equal(1, windowsProxy.DisableCount);
        Assert.Empty(windowsProxy.EnabledServers);
    }

    /// <summary>Verifies a stopped transparent proxy service falls back to system proxy.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTransparentProxyServiceInstalledButStopped_FallsBackToSystemProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(true, false, "Stopped"));
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.FullTakeover,
            true,
            10002,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.FullTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal("missing full", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.FullTakeover, false, 10002)], configuration.Requests);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.Equal(["127.0.0.1:10002"], windowsProxy.EnabledServers);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    /// <summary>Verifies missing transparent proxy service falls back to system proxy.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTransparentProxyEnabledButServiceMissing_FallsBackToSystemProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        FakeNetworkTakeoverMihomoService serviceStatus = new(new MihomoServiceStatus(false, false, "Missing"));
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: serviceStatus);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            true,
            10001,
            CancellationToken.None);

        Assert.Equal(ClashSharpMode.RuleTakeover, result.Mode);
        Assert.True(result.CoreRunning);
        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal("missing rule", result.Message);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.RuleTakeover, false, 10001)], configuration.Requests);
        Assert.Equal([configuration.State], core.RestartedStates);
        Assert.Equal(["127.0.0.1:10001"], windowsProxy.EnabledServers);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    /// <summary>Verifies disabled mode stops mihomo and disables Windows system proxy through dependencies.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenDisabled_StopsCoreAndDisablesSystemProxy()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        NetworkTakeoverService service = CreateService(configuration: configuration, core: core, windowsProxy: windowsProxy);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.Disabled,
            false,
            7890,
            CancellationToken.None);

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

    /// <summary>Verifies journaled parameters win over settings that changed after planning.</summary>
    [Fact]
    public async Task ApplyModeAsync_WithExplicitPlan_UsesFrozenTunAndPortValues()
    {
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            windowsProxy: windowsProxy);

        NetworkTakeoverResult result = await service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            mixedPort: 12000,
            CancellationToken.None);

        Assert.True(result.SystemProxyEnabled);
        Assert.False(result.TransparentProxyEnabled);
        Assert.Equal([new ConfigurationRequest(ClashSharpMode.RuleTakeover, false, 12000)], configuration.Requests);
        Assert.Equal(["127.0.0.1:12000"], windowsProxy.EnabledServers);
    }

    [Fact]
    public async Task ApplyModeAsync_CancelledAfterServiceQuery_DoesNotMutateNetworkState()
    {
        using CancellationTokenSource cancellation = new();
        FakeNetworkTakeoverCoreConfiguration configuration = new();
        FakeNetworkTakeoverCore core = new();
        FakeNetworkTakeoverWindowsProxy windowsProxy = new();
        NetworkTakeoverService service = CreateService(
            configuration: configuration,
            core: core,
            windowsProxy: windowsProxy,
            serviceStatus: new CancellingNetworkTakeoverMihomoService(cancellation));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ApplyModeAsync(
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            mixedPort: 12000,
            cancellation.Token));

        Assert.Empty(configuration.Requests);
        Assert.Empty(core.RestartedStates);
        Assert.False(core.Stopped);
        Assert.Empty(windowsProxy.EnabledServers);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    private static NetworkTakeoverService CreateService(
        FakeNetworkTakeoverCoreConfiguration? configuration = null,
        FakeNetworkTakeoverCore? core = null,
        FakeNetworkTakeoverWindowsProxy? windowsProxy = null,
        INetworkTakeoverMihomoService? serviceStatus = null,
        FakeNetworkTakeoverProxyRecovery? proxyRecovery = null)
    {
        return new NetworkTakeoverService(
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

    private sealed class FakeNetworkTakeoverCoreConfiguration : INetworkTakeoverCoreConfiguration
    {
        public CoreConfigurationState State { get; } = new(@"C:\mihomo", @"C:\mihomo\config.yaml", true);

        public List<ConfigurationRequest> Requests { get; } = [];

        public CoreConfigurationState EnsureConfiguration(
            ClashSharpMode mode,
            bool transparentProxyEnabled,
            int mixedPort)
        {
            Requests.Add(new ConfigurationRequest(mode, transparentProxyEnabled, mixedPort));
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
        public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(status);
        }
    }

    private sealed class CancellingNetworkTakeoverMihomoService(CancellationTokenSource cancellation)
        : INetworkTakeoverMihomoService
    {
        public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            cancellation.Cancel();
            return Task.FromResult(new MihomoServiceStatus(true, true, "running"));
        }
    }

    private sealed class FakeNetworkTakeoverProxyRecovery : INetworkTakeoverProxyRecovery
    {
        public string BuildLoopbackProxyServer(int mixedPort)
        {
            return $"127.0.0.1:{mixedPort}";
        }
    }

    private readonly record struct ConfigurationRequest(
        ClashSharpMode Mode,
        bool TransparentProxyEnabled,
        int MixedPort);
}
