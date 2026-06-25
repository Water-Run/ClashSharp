/*
 * Mihomo Service Manager Tests
 * Verifies transparent-proxy Windows service status handling without invoking sc.exe
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/MihomoServiceManagerTests.cs
 * @date: 2026-06-25
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for mihomo Windows service management.</summary>
public sealed class MihomoServiceManagerTests
{
    /// <summary>Verifies a failed sc.exe query maps to a localized not-deployed status.</summary>
    [Fact]
    public void GetStatus_WhenScQueryFails_ReturnsLocalizedNotDeployedStatus()
    {
        FakeMihomoServiceCommandRunner runner = new()
        {
            QueryResult = new MihomoServiceCommandResult(1060, string.Empty),
        };
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = manager.GetStatus();

        Assert.False(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.Equal("not deployed", status.Message);
        Assert.Equal([["query", MihomoServiceManager.ServiceName]], runner.RunScCalls);
    }

    /// <summary>Verifies a running service query maps to a localized running status.</summary>
    [Fact]
    public void GetStatus_WhenScQueryReportsRunning_ReturnsLocalizedRunningStatus()
    {
        FakeMihomoServiceCommandRunner runner = new()
        {
            QueryResult = new MihomoServiceCommandResult(0, "STATE              : 4  RUNNING"),
        };
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = manager.GetStatus();

        Assert.True(status.IsInstalled);
        Assert.True(status.IsRunning);
        Assert.Equal("running", status.Message);
    }

    /// <summary>Verifies an installed stopped service maps to a localized deployed status.</summary>
    [Fact]
    public void GetStatus_WhenScQueryReportsStopped_ReturnsLocalizedDeployedStatus()
    {
        FakeMihomoServiceCommandRunner runner = new()
        {
            QueryResult = new MihomoServiceCommandResult(0, "STATE              : 1  STOPPED"),
        };
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = manager.GetStatus();

        Assert.True(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.Equal("deployed", status.Message);
    }

    private static MihomoServiceManager CreateManager(FakeMihomoServiceCommandRunner runner)
    {
        return new MihomoServiceManager(
            runner,
            new FakeMihomoServiceDeploymentContext(),
            key => key switch
            {
                "MihomoService.Status.NotDeployed" => "not deployed",
                "MihomoService.Status.DeployedRunning" => "running",
                "MihomoService.Status.Deployed" => "deployed",
                _ => key,
            });
    }

    private sealed class FakeMihomoServiceCommandRunner : IMihomoServiceCommandRunner
    {
        public MihomoServiceCommandResult QueryResult { get; init; }

        public List<IReadOnlyList<string>> RunScCalls { get; } = [];

        public MihomoServiceCommandResult RunSc(params string[] arguments)
        {
            RunScCalls.Add(arguments);
            return QueryResult;
        }

        public Task<MihomoServiceCommandResult> RunScElevatedAsync(CancellationToken cancellationToken, params string[] arguments)
        {
            return Task.FromResult(new MihomoServiceCommandResult(0, string.Empty));
        }
    }

    private sealed class FakeMihomoServiceDeploymentContext : IMihomoServiceDeploymentContext
    {
        public string? ResolveServiceHostPath()
        {
            return null;
        }

        public string MihomoBinaryPath => @"C:\mihomo.exe";

        public CoreConfigurationState EnsureTransparentProxyConfiguration()
        {
            return new CoreConfigurationState(@"C:\mihomo", @"C:\mihomo\config.yaml", true);
        }
    }
}
