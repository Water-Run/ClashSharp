/*
 * Startup Conflict Detection Service Tests
 * Verifies startup conflict discovery and repair actions without touching the host machine
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/StartupConflictDetectionServiceTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.Service;
using System.Reflection;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests startup conflict detection and repair routing.</summary>
public sealed class StartupConflictDetectionServiceTests
{
    /// <summary>Verifies startup checks report external cores, occupied ports, and wrong manual proxy ports.</summary>
    [Fact]
    public void CheckConflicts_ReportsEveryDetectedProblem()
    {
        FakeStartupConflictEnvironment environment = new()
        {
            Processes = [new StartupConflictProcess(42, "mihomo")],
            IsPortInUse = true,
            ProxyState = new WindowsProxyState(true, "127.0.0.1:7890"),
        };
        StartupConflictDetectionService service = new(environment);

        IReadOnlyList<StartupConflictIssue> issues = service.CheckConflicts(10000);

        Assert.Collection(
            issues,
            issue => Assert.Equal(StartupConflictKind.ExternalMihomoProcess, issue.Kind),
            issue => Assert.Equal(StartupConflictKind.MixedPortOccupied, issue.Kind),
            issue => Assert.Equal(StartupConflictKind.WindowsProxyWrongPort, issue.Kind));
    }

    /// <summary>Verifies a matching manual proxy port is not treated as a conflict.</summary>
    [Fact]
    public void CheckConflicts_AllowsManualProxyUsingTargetPort()
    {
        FakeStartupConflictEnvironment environment = new()
        {
            ProxyState = new WindowsProxyState(true, "127.0.0.1:10000"),
        };
        StartupConflictDetectionService service = new(environment);

        IReadOnlyList<StartupConflictIssue> issues = service.CheckConflicts(10000);

        Assert.Empty(issues);
    }

    /// <summary>Verifies loopback and target port must belong to the same endpoint before treating a proxy as Clash# owned.</summary>
    [Fact]
    public void CheckConflicts_WhenLoopbackAndTargetPortAreOnDifferentEndpoints_ReportsWrongManualProxy()
    {
        FakeStartupConflictEnvironment environment = new()
        {
            ProxyState = new WindowsProxyState(true, "http=127.0.0.1:18080;https=corp-proxy:10000"),
        };
        StartupConflictDetectionService service = new(environment);

        IReadOnlyList<StartupConflictIssue> issues = service.CheckConflicts(10000);

        StartupConflictIssue issue = Assert.Single(issues);
        Assert.Equal(StartupConflictKind.WindowsProxyWrongPort, issue.Kind);
    }

    /// <summary>Verifies per-issue repair actions update host state and report success or failure.</summary>
    [Fact]
    public async Task RepairAsync_PerIssueActionReportsStatus()
    {
        FakeStartupConflictEnvironment environment = new()
        {
            Processes = [new StartupConflictProcess(42, "mihomo")],
            IsPortInUse = true,
            ProxyState = new WindowsProxyState(true, "127.0.0.1:7890"),
        };
        StartupConflictDetectionService service = new(environment);
        IReadOnlyList<StartupConflictIssue> issues = service.CheckConflicts(10000);

        StartupConflictRepairResult processResult = await issues[0].RepairAsync(CancellationToken.None);
        StartupConflictRepairResult portResult = await issues[1].RepairAsync(CancellationToken.None);
        StartupConflictRepairResult proxyResult = await issues[2].RepairAsync(CancellationToken.None);

        Assert.True(processResult.Succeeded);
        Assert.Contains(42, environment.TerminatedProcessIds);
        Assert.False(portResult.Succeeded);
        Assert.True(proxyResult.Succeeded);
        Assert.True(environment.ProxyDisabled);
    }

    /// <summary>Verifies user-facing issue text is resolved through an injected localizer.</summary>
    [Fact]
    public async Task CheckConflicts_UsesInjectedLocalizerForIssueTextAndRepairResults()
    {
        FakeStartupConflictEnvironment environment = new()
        {
            Processes = [new StartupConflictProcess(42, "mihomo")],
            IsPortInUse = true,
            ProxyState = new WindowsProxyState(true, "127.0.0.1:7890"),
        };
        StartupConflictDetectionService service = CreateService(environment, key => key switch
        {
            "StartupConflict.Mihomo.Title" => "mihomo title",
            "StartupConflict.Mihomo.Description" => "mihomo count {0}",
            "StartupConflict.Mihomo.Repair" => "mihomo repair",
            "StartupConflict.Mihomo.RepairSucceeded" => "mihomo repaired",
            "StartupConflict.Port.Title" => "port title",
            "StartupConflict.Port.Description" => "port {0}",
            "StartupConflict.Port.Repair" => "port repair",
            "StartupConflict.Port.RepairFailed" => "port failed",
            "StartupConflict.Proxy.Title" => "proxy title",
            "StartupConflict.Proxy.Description" => "proxy {0} {1}",
            "StartupConflict.Proxy.Repair" => "proxy repair",
            "StartupConflict.Proxy.RepairSucceeded" => "proxy repaired",
            _ => key,
        });

        IReadOnlyList<StartupConflictIssue> issues = service.CheckConflicts(10000);

        Assert.Equal("mihomo title", issues[0].Title);
        Assert.Equal("mihomo count 1", issues[0].Description);
        Assert.Equal("mihomo repair", issues[0].RepairText);
        Assert.Equal("port title", issues[1].Title);
        Assert.Equal("port 10000", issues[1].Description);
        Assert.Equal("proxy title", issues[2].Title);
        Assert.Equal("proxy 127.0.0.1:7890 10000", issues[2].Description);
        Assert.Equal("mihomo repaired", (await issues[0].RepairAsync(CancellationToken.None)).Message);
        Assert.Equal("port failed", (await issues[1].RepairAsync(CancellationToken.None)).Message);
        Assert.Equal("proxy repaired", (await issues[2].RepairAsync(CancellationToken.None)).Message);
    }

    private static StartupConflictDetectionService CreateService(
        IStartupConflictEnvironment environment,
        Func<string, string> getString)
    {
        ConstructorInfo? constructor = typeof(StartupConflictDetectionService).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(IStartupConflictEnvironment),
                typeof(Func<string, string>),
            ],
            modifiers: null);
        Assert.NotNull(constructor);
        return Assert.IsType<StartupConflictDetectionService>(constructor.Invoke([environment, getString]));
    }

    private sealed class FakeStartupConflictEnvironment : IStartupConflictEnvironment
    {
        public IReadOnlyList<StartupConflictProcess> Processes { get; init; } = [];

        public bool IsPortInUse { get; init; }

        public WindowsProxyState ProxyState { get; init; } = new(false, string.Empty);

        public List<int> TerminatedProcessIds { get; } = [];

        public bool ProxyDisabled { get; private set; }

        public IReadOnlyList<StartupConflictProcess> GetExternalMihomoProcesses()
        {
            return Processes;
        }

        public bool IsTcpPortInUse(int port)
        {
            return IsPortInUse;
        }

        public WindowsProxyState GetWindowsProxyState()
        {
            return ProxyState;
        }

        public Task TerminateProcessAsync(int processId, CancellationToken cancellationToken)
        {
            TerminatedProcessIds.Add(processId);
            return Task.CompletedTask;
        }

        public Task DisableWindowsProxyAsync(CancellationToken cancellationToken)
        {
            ProxyDisabled = true;
            return Task.CompletedTask;
        }
    }
}
