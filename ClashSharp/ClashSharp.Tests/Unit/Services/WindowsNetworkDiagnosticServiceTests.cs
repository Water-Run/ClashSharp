/*
 * Windows Network Diagnostic Service Tests
 * Verifies Windows-native diagnostics without touching the real user environment
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/WindowsNetworkDiagnosticServiceTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for Windows network diagnostics.</summary>
public sealed class WindowsNetworkDiagnosticServiceTests
{
    /// <summary>Verifies terminal diagnosis reads injected environment variables and localized text.</summary>
    [Fact]
    public async Task DiagnoseAsync_WhenTerminalProxyEnvironmentMatchesConfiguredPort_ReturnsReady()
    {
        FakeWindowsDiagnosticEnvironment environment = new()
        {
            Variables =
            {
                ["HTTP_PROXY"] = "http://127.0.0.1:19090",
                ["HTTPS_PROXY"] = "http://127.0.0.1:19090",
                ["ALL_PROXY"] = "http://127.0.0.1:19090",
                ["NO_PROXY"] = "localhost,127.0.0.1,::1",
            },
        };
        WindowsNetworkDiagnosticService service = CreateService(environment);

        WindowsDiagnosticResult result = await service.DiagnoseAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.Equal(WindowsDiagnosticTarget.Terminal, result.Target);
        Assert.Equal("terminal target", result.DisplayName);
        Assert.True(result.IsHealthy);
        Assert.Equal("terminal ready", result.Message);
        Assert.Contains("HTTP_PROXY=http://127.0.0.1:19090", result.Detail, StringComparison.Ordinal);
    }

    /// <summary>Verifies applying terminal repair writes proxy environment variables through the injected environment.</summary>
    [Fact]
    public async Task ApplyAsync_WhenTerminalTarget_SetsProxyEnvironmentVariablesAndReturnsReady()
    {
        FakeWindowsDiagnosticEnvironment environment = new();
        WindowsNetworkDiagnosticService service = CreateService(environment);

        WindowsDiagnosticResult result = await service.ApplyAsync(WindowsDiagnosticTarget.Terminal, CancellationToken.None);

        Assert.True(result.IsHealthy);
        Assert.Equal("terminal ready", result.Message);
        Assert.Equal("http://127.0.0.1:19090", environment.Variables["HTTP_PROXY"]);
        Assert.Equal("http://127.0.0.1:19090", environment.Variables["HTTPS_PROXY"]);
        Assert.Equal("http://127.0.0.1:19090", environment.Variables["ALL_PROXY"]);
        Assert.Equal("localhost,127.0.0.1,::1", environment.Variables["NO_PROXY"]);
    }

    private static WindowsNetworkDiagnosticService CreateService(FakeWindowsDiagnosticEnvironment environment)
    {
        return new WindowsNetworkDiagnosticService(
            new FakeWindowsDiagnosticSettings { MixedPort = 19090 },
            environment,
            new FakeWindowsDiagnosticProcessRunner(),
            key => key switch
            {
                "WindowsDiagnostic.Target.Terminal" => "terminal target",
                "WindowsDiagnostic.Terminal.Ready" => "terminal ready",
                "WindowsDiagnostic.Terminal.ProxyEnvironmentMissing" => "terminal missing",
                _ => key,
            });
    }

    private sealed class FakeWindowsDiagnosticSettings : IWindowsDiagnosticSettings
    {
        public int MixedPort { get; init; }
    }

    private sealed class FakeWindowsDiagnosticEnvironment : IWindowsDiagnosticEnvironment
    {
        public Dictionary<string, string?> Variables { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetUserEnvironmentVariable(string name)
        {
            return Variables.GetValueOrDefault(name);
        }

        public void SetUserEnvironmentVariable(string name, string? value)
        {
            if (value is null)
            {
                Variables.Remove(name);
                return;
            }

            Variables[name] = value;
        }
    }

    private sealed class FakeWindowsDiagnosticProcessRunner : IWindowsDiagnosticProcessRunner
    {
        public Task<WindowsDiagnosticProcessResult> RunAsync(
            string fileName,
            IReadOnlyList<string> arguments,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(new WindowsDiagnosticProcessResult(0, string.Empty, string.Empty));
        }
    }
}
