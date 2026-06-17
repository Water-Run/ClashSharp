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
