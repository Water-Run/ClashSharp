/*
 * Proxy Recovery Service Tests
 * Verifies stale Windows proxy recovery through injected dependencies
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/ProxyRecoveryServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for startup stale-proxy recovery.</summary>
public sealed class ProxyRecoveryServiceTests
{
    /// <summary>Verifies disabled startup recovery avoids reading or mutating Windows proxy state.</summary>
    [Fact]
    public void ApplyStartupRecoveryIfNeeded_WhenStartupCheckDisabled_SkipsWindowsProxy()
    {
        FakeProxyRecoverySettings settings = new()
        {
            CheckStaleProxyOnStartup = false,
            MixedPort = 19090,
        };
        FakeProxyRecoveryWindowsProxy windowsProxy = new(new WindowsProxyState(true, "127.0.0.1:19090"));
        ProxyRecoveryService service = CreateService(settings, windowsProxy);

        ProxyRecoveryResult result = service.ApplyStartupRecoveryIfNeeded();

        Assert.False(result.WasApplied);
        Assert.Equal("check disabled", result.Message);
        Assert.Equal(0, windowsProxy.ReadCount);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    /// <summary>Verifies stale system proxy detection can disable Windows proxy through an injected boundary.</summary>
    [Fact]
    public void ApplyStartupRecoveryIfNeeded_WhenStaleProxyAndDisablePolicy_DisablesWindowsProxy()
    {
        FakeProxyRecoverySettings settings = new()
        {
            CheckStaleProxyOnStartup = true,
            MixedPort = 19090,
        };
        FakeProxyRecoveryWindowsProxy windowsProxy = new(new WindowsProxyState(true, "http=127.0.0.1:19090;https=localhost:19090"));
        ProxyRecoveryService service = CreateService(settings, windowsProxy);

        ProxyRecoveryResult result = service.ApplyStartupRecoveryIfNeeded();

        Assert.True(result.WasApplied);
        Assert.Equal("disabled stale", result.Message);
        Assert.Equal(1, windowsProxy.ReadCount);
        Assert.Equal(1, windowsProxy.DisableCount);
    }

    /// <summary>Verifies loopback and target port must belong to the same proxy endpoint.</summary>
    [Fact]
    public void IsStaleClashProxy_WhenLoopbackAndTargetPortAreOnDifferentEndpoints_ReturnsFalse()
    {
        ProxyRecoveryService service = CreateService();
        WindowsProxyState state = new(true, "http=127.0.0.1:18080;https=corp-proxy:19090");

        bool isStale = service.IsStaleClashProxy(state, 19090);

        Assert.False(isStale);
    }

    private static ProxyRecoveryService CreateService(
        FakeProxyRecoverySettings? settings = null,
        FakeProxyRecoveryWindowsProxy? windowsProxy = null)
    {
        return new ProxyRecoveryService(
            settings ?? new FakeProxyRecoverySettings(),
            windowsProxy ?? new FakeProxyRecoveryWindowsProxy(new WindowsProxyState(false, string.Empty)),
            key => key switch
            {
                "ProxyRecovery.CheckDisabled" => "check disabled",
                "ProxyRecovery.NoStaleProxy" => "no stale",
                "ProxyRecovery.Disabled" => "disabled stale",
                _ => key,
            });
    }

    private sealed class FakeProxyRecoverySettings : IProxyRecoverySettings
    {
        public bool CheckStaleProxyOnStartup { get; init; } = true;

        public int MixedPort { get; init; } = 7890;
    }

    private sealed class FakeProxyRecoveryWindowsProxy(WindowsProxyState state) : IProxyRecoveryWindowsProxy
    {
        public int ReadCount { get; private set; }

        public int DisableCount { get; private set; }

        public WindowsProxyState GetCurrentState()
        {
            ReadCount++;
            return state;
        }

        public void DisableProxy()
        {
            DisableCount++;
        }
    }
}
