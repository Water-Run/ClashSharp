using ClashSharp.ApplicationModel.Processes;
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
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(1060, standardOutput: "service does not exist"));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = manager.GetStatus();

        Assert.False(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.Equal("not deployed", status.Message);
        ProcessRequest request = Assert.Single(runner.Requests);
        Assert.Equal("sc.exe", request.FileName);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], request.Arguments);
        Assert.False(request.RunElevated);
    }

    /// <summary>Verifies a running service query maps to a localized running status.</summary>
    [Fact]
    public void GetStatus_WhenScQueryReportsRunning_ReturnsLocalizedRunningStatus()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = manager.GetStatus();

        Assert.True(status.IsInstalled);
        Assert.True(status.IsRunning);
        Assert.Equal("running", status.Message);
    }

    /// <summary>Verifies deployment trusts the mandatory final SCM query even after a non-zero command exit.</summary>
    [Fact]
    public async Task DeployAsync_WhenCreateExitIsNonzeroButScmReportsInstalled_ReturnsObservedStatus()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(1060));
        runner.Results.Enqueue(Completed(5, standardError: "access denied"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        MihomoServiceManager manager = CreateManager(runner, serviceHostPath: @"C:\Program Files\ClashSharp\service.exe");

        MihomoServiceStatus status = await manager.DeployAsync(CancellationToken.None);

        Assert.True(status.IsInstalled);
        Assert.True(status.IsRunning);
        Assert.Equal(3, runner.Requests.Count);
        ProcessRequest create = runner.Requests[1];
        Assert.True(create.RunElevated);
        Assert.Equal("create", create.Arguments[0]);
        Assert.Equal(MihomoServiceManager.ServiceName, create.Arguments[1]);
        Assert.Equal("binPath=", create.Arguments[2]);
        Assert.Contains("\"C:\\Program Files\\ClashSharp\\service.exe\"", create.Arguments[3], StringComparison.Ordinal);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], runner.Requests[2].Arguments);
    }

    /// <summary>Verifies an elevated cancellation is re-queried and then propagated as cancellation.</summary>
    [Fact]
    public async Task DeployAsync_WhenCreateIsCancelled_RequeriesScmThenThrowsCancellation()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(1060));
        runner.Results.Enqueue(Result(ProcessRunOutcome.Cancelled));
        runner.Results.Enqueue(Completed(1060));
        MihomoServiceManager manager = CreateManager(runner, serviceHostPath: @"C:\service.exe");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DeployAsync(new CancellationToken(canceled: true)));

        Assert.Equal(3, runner.Requests.Count);
        Assert.False(runner.Requests[2].RunElevated);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], runner.Requests[2].Arguments);
    }

    /// <summary>Verifies an inconclusive initial query prevents an unsafe deployment attempt.</summary>
    [Fact]
    public async Task DeployAsync_WhenInitialQueryTimesOut_DoesNotRunElevatedCommand()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Result(ProcessRunOutcome.TimedOut));
        MihomoServiceManager manager = CreateManager(runner, serviceHostPath: @"C:\service.exe");

        MihomoServiceStatus status = await manager.DeployAsync(CancellationToken.None);

        Assert.False(status.IsInstalled);
        Assert.Equal("deployment failed", status.Message);
        Assert.Single(runner.Requests);
        Assert.False(runner.Requests[0].RunElevated);
    }

    /// <summary>Verifies uninstall re-queries after stop and delete and treats externally absent service as success.</summary>
    [Fact]
    public async Task UninstallAsync_WhenDeleteExitIsNonzeroButScmReportsAbsent_ReturnsNotDeployed()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        runner.Results.Enqueue(Completed(1060, standardError: "marked for deletion"));
        runner.Results.Enqueue(Completed(1060, standardOutput: "service does not exist"));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.UninstallAsync(CancellationToken.None);

        Assert.False(status.IsInstalled);
        Assert.Equal("not deployed", status.Message);
        Assert.Equal(5, runner.Requests.Count);
        Assert.Equal(["stop", MihomoServiceManager.ServiceName], runner.Requests[1].Arguments);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], runner.Requests[2].Arguments);
        Assert.Equal(["delete", MihomoServiceManager.ServiceName], runner.Requests[3].Arguments);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], runner.Requests[4].Arguments);
    }

    /// <summary>Verifies an externally removed service after stop avoids a redundant delete operation.</summary>
    [Fact]
    public async Task UninstallAsync_WhenServiceDisappearsAfterStop_ReturnsIdempotentSuccess()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(1060));
        runner.Results.Enqueue(Completed(1060));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.UninstallAsync(CancellationToken.None);

        Assert.False(status.IsInstalled);
        Assert.Equal(3, runner.Requests.Count);
        Assert.DoesNotContain(runner.Requests, request => request.Arguments.Contains("delete", StringComparer.Ordinal));
    }

    /// <summary>Verifies an inconclusive final query cannot be reported as successful removal.</summary>
    [Fact]
    public async Task UninstallAsync_WhenFinalQueryTimesOut_ReturnsRemovalFailure()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Result(ProcessRunOutcome.TimedOut));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.UninstallAsync(CancellationToken.None);

        Assert.True(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.Equal("removal failed", status.Message);
        Assert.Equal(5, runner.Requests.Count);
    }

    /// <summary>Verifies an inconclusive initial query prevents a false idempotent uninstall success.</summary>
    [Fact]
    public async Task UninstallAsync_WhenInitialQueryTimesOut_DoesNotClaimSuccessOrRunElevatedCommand()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Result(ProcessRunOutcome.TimedOut));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.UninstallAsync(CancellationToken.None);

        Assert.False(status.IsInstalled);
        Assert.Equal("removal failed", status.Message);
        Assert.Single(runner.Requests);
        Assert.False(runner.Requests[0].RunElevated);
    }

    private static MihomoServiceManager CreateManager(
        FakeProcessRunner runner,
        string? serviceHostPath = null)
    {
        return new MihomoServiceManager(
            runner,
            new FakeMihomoServiceDeploymentContext(serviceHostPath),
            key => key switch
            {
                "MihomoService.Status.NotDeployed" => "not deployed",
                "MihomoService.Status.DeployedRunning" => "running",
                "MihomoService.Status.Deployed" => "deployed",
                "MihomoService.Status.DeploymentFailed" => "deployment failed",
                "MihomoService.Status.RemovalFailed" => "removal failed",
                _ => key,
            });
    }

    private static ProcessRunResult Completed(
        int exitCode,
        string standardOutput = "",
        string standardError = "")
    {
        return new ProcessRunResult(
            ProcessRunOutcome.Completed,
            exitCode,
            42,
            standardOutput,
            standardError,
            null);
    }

    private static ProcessRunResult Result(ProcessRunOutcome outcome)
    {
        return new ProcessRunResult(outcome, null, 42, string.Empty, string.Empty, null);
    }

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public Queue<ProcessRunResult> Results { get; } = [];

        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(Results.Dequeue());
        }
    }

    private sealed class FakeMihomoServiceDeploymentContext(string? serviceHostPath) : IMihomoServiceDeploymentContext
    {
        public string? ResolveServiceHostPath()
        {
            return serviceHostPath;
        }

        public string MihomoBinaryPath => @"C:\mihomo.exe";

        public CoreConfigurationState EnsureTransparentProxyConfiguration()
        {
            return new CoreConfigurationState(@"C:\mihomo", @"C:\mihomo\config.yaml", true);
        }
    }
}
