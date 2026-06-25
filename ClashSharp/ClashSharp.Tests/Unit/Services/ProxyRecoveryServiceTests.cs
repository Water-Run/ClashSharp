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
            ProxyRecoveryMode = ProxyRecoveryMode.DisableProxy,
        };
        FakeProxyRecoveryWindowsProxy windowsProxy = new(new WindowsProxyState(true, "127.0.0.1:19090"));
        FakeProxyRecoveryTakeover takeover = new();
        ProxyRecoveryService service = CreateService(settings, windowsProxy, takeover);

        ProxyRecoveryResult result = service.ApplyStartupRecoveryIfNeeded();

        Assert.False(result.WasApplied);
        Assert.Equal("check disabled", result.Message);
        Assert.Equal(0, windowsProxy.ReadCount);
        Assert.Equal(0, windowsProxy.DisableCount);
        Assert.Equal(0, takeover.ApplyCount);
    }

    /// <summary>Verifies stale system proxy detection can disable Windows proxy through an injected boundary.</summary>
    [Fact]
    public void ApplyStartupRecoveryIfNeeded_WhenStaleProxyAndDisablePolicy_DisablesWindowsProxy()
    {
        FakeProxyRecoverySettings settings = new()
        {
            CheckStaleProxyOnStartup = true,
            MixedPort = 19090,
            ProxyRecoveryMode = ProxyRecoveryMode.DisableProxy,
        };
        FakeProxyRecoveryWindowsProxy windowsProxy = new(new WindowsProxyState(true, "http=127.0.0.1:19090;https=localhost:19090"));
        ProxyRecoveryService service = CreateService(settings, windowsProxy);

        ProxyRecoveryResult result = service.ApplyStartupRecoveryIfNeeded();

        Assert.True(result.WasApplied);
        Assert.Equal("disabled stale", result.Message);
        Assert.Equal(1, windowsProxy.ReadCount);
        Assert.Equal(1, windowsProxy.DisableCount);
    }

    /// <summary>Verifies stale system proxy recovery delegates restore-on behavior to the takeover boundary.</summary>
    [Fact]
    public void ApplyStartupRecoveryIfNeeded_WhenStaleProxyAndEnablePolicy_UsesTakeoverRecovery()
    {
        FakeProxyRecoverySettings settings = new()
        {
            CheckStaleProxyOnStartup = true,
            MixedPort = 19090,
            ProxyRecoveryMode = ProxyRecoveryMode.EnableProxy,
        };
        FakeProxyRecoveryWindowsProxy windowsProxy = new(new WindowsProxyState(true, "127.0.0.1:19090"));
        FakeProxyRecoveryTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(ClashSharpMode.RuleTakeover, true, true, false, "takeover recovered"),
        };
        ProxyRecoveryService service = CreateService(settings, windowsProxy, takeover);

        ProxyRecoveryResult result = service.ApplyStartupRecoveryIfNeeded();

        Assert.True(result.WasApplied);
        Assert.Equal("takeover recovered", result.Message);
        Assert.Equal(1, takeover.ApplyCount);
        Assert.Equal(0, windowsProxy.DisableCount);
    }

    /// <summary>Verifies restore-on failure disables stale Windows proxy before preserving the original exception.</summary>
    [Fact]
    public void ApplyStartupRecoveryIfNeeded_WhenEnablePolicyFails_DisablesProxyAndRethrows()
    {
        InvalidOperationException expected = new("mihomo could not start");
        FakeProxyRecoverySettings settings = new()
        {
            CheckStaleProxyOnStartup = true,
            MixedPort = 19090,
            ProxyRecoveryMode = ProxyRecoveryMode.EnableProxy,
        };
        FakeProxyRecoveryWindowsProxy windowsProxy = new(new WindowsProxyState(true, "127.0.0.1:19090"));
        FakeProxyRecoveryTakeover takeover = new()
        {
            Exception = expected,
        };
        ProxyRecoveryService service = CreateService(settings, windowsProxy, takeover);

        InvalidOperationException actual = Assert.Throws<InvalidOperationException>(() => service.ApplyStartupRecoveryIfNeeded());

        Assert.Same(expected, actual);
        Assert.Equal(1, takeover.ApplyCount);
        Assert.Equal(1, windowsProxy.DisableCount);
    }

    private static ProxyRecoveryService CreateService(
        FakeProxyRecoverySettings? settings = null,
        FakeProxyRecoveryWindowsProxy? windowsProxy = null,
        FakeProxyRecoveryTakeover? takeover = null)
    {
        return new ProxyRecoveryService(
            settings ?? new FakeProxyRecoverySettings(),
            windowsProxy ?? new FakeProxyRecoveryWindowsProxy(new WindowsProxyState(false, string.Empty)),
            takeover ?? new FakeProxyRecoveryTakeover(),
            key => key switch
            {
                "ProxyRecovery.CheckDisabled" => "check disabled",
                "ProxyRecovery.NoStaleProxy" => "no stale",
                "ProxyRecovery.Disabled" => "disabled stale",
                "ProxyRecovery.DoNothing" => "do nothing",
                _ => key,
            });
    }

    private sealed class FakeProxyRecoverySettings : IProxyRecoverySettings
    {
        public bool CheckStaleProxyOnStartup { get; init; } = true;

        public int MixedPort { get; init; } = 7890;

        public ProxyRecoveryMode ProxyRecoveryMode { get; init; } = ProxyRecoveryMode.DoNothing;
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

    private sealed class FakeProxyRecoveryTakeover : IProxyRecoveryTakeover
    {
        public int ApplyCount { get; private set; }

        public Exception? Exception { get; init; }

        public NetworkTakeoverResult Result { get; init; } = new(ClashSharpMode.RuleTakeover, true, true, false, "recovered");

        public NetworkTakeoverResult ApplyStartupSystemProxyRecovery()
        {
            ApplyCount++;
            if (Exception is not null)
            {
                throw Exception;
            }

            return Result;
        }
    }
}
