using ClashSharp.ApplicationModel.Processes;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for mihomo Windows service management.</summary>
public sealed class MihomoServiceManagerTests
{
    private const long Generation = 17;

    private const string ConfigurationHash =
        "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string IpcToken =
        "abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789";

    /// <summary>Verifies a manager does not report service absence before SCM has been queried.</summary>
    [Fact]
    public void GetLatestStatus_BeforeFirstQuery_ReturnsUnknownStatus()
    {
        FakeProcessRunner runner = new();
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = manager.GetLatestStatus();

        Assert.False(status.IsKnown);
        Assert.False(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.Equal("unknown", status.Message);
        Assert.Empty(runner.Requests);
    }

    /// <summary>Verifies a failed sc.exe query maps to a localized not-deployed status.</summary>
    [Fact]
    public async Task GetStatusAsync_WhenScQueryFails_ReturnsLocalizedNotDeployedStatus()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(1060, standardOutput: "service does not exist"));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.False(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.Equal("not deployed", status.Message);
        ProcessRequest request = Assert.Single(runner.Requests);
        Assert.Equal("sc.exe", request.FileName);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], request.Arguments);
        Assert.False(request.RunElevated);
    }

    /// <summary>Verifies an invalid Installer association blocks all SCM lifecycle entry points.</summary>
    [Fact]
    public async Task LifecycleOperations_WhenAssociationIsInvalid_FailClosedWithoutScmOrIpc()
    {
        FakeProcessRunner runner = new();
        FakeMihomoServiceIpcClient ipc = new();
        MihomoServiceIpcEndpoint endpoint = MihomoServiceIpcEndpoint.Unprovisioned(
            MihomoServiceIpcEndpoint.AssociationInvalidCode);
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc, endpoint: endpoint);

        MihomoServiceStatus initial = manager.GetLatestStatus();
        MihomoServiceStatus queried = await manager.GetStatusAsync(CancellationToken.None);
        MihomoServiceStatus restarted = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);
        MihomoServiceStatus stopped = await manager.StopAsync(CancellationToken.None);
        MihomoServiceStatus deployed = await manager.DeployAsync(CancellationToken.None);
        MihomoServiceStatus uninstalled = await manager.UninstallAsync(CancellationToken.None);

        Assert.All(
            new[] { initial, queried, restarted, stopped, deployed, uninstalled },
            status =>
            {
                Assert.False(status.IsKnown);
                Assert.Equal(
                    MihomoServiceIpcEndpoint.AssociationInvalidCode,
                    status.ProvisioningFailureCode);
            });
        Assert.Empty(runner.Requests);
        Assert.Empty(ipc.Requests);
    }

    /// <summary>Verifies diagnostics and typed controller capabilities cannot reach the sentinel pipe.</summary>
    [Fact]
    public async Task IpcCapabilities_WhenAssociationOwnerMismatches_FailClosedWithoutTransport()
    {
        FakeProcessRunner runner = new();
        FakeMihomoServiceIpcClient ipc = new();
        MihomoServiceIpcEndpoint endpoint = MihomoServiceIpcEndpoint.Unprovisioned(
            MihomoServiceIpcEndpoint.OwnerMismatchCode);
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc, endpoint: endpoint);
        MihomoServiceIpcControllerBinding binding = new()
        {
            ServiceSessionId = Guid.NewGuid(),
            Generation = Generation,
            ConfigurationHash = ConfigurationHash,
        };

        InvalidOperationException logsFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => manager.ReadHostLogsAsync(CancellationToken.None));
        InvalidOperationException controllerFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => manager.SendControllerIpcAsync(
                MihomoServiceIpcCommand.GetConnections,
                binding,
                connectionId: null,
                proxySelection: null,
                runtimeLogQuery: null,
                CancellationToken.None));
        InvalidOperationException providerFailure = await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => manager.UpdateProviderIpcAsync(
                binding,
                new MihomoServiceIpcProviderUpdate
                {
                    Kind = MihomoServiceIpcProviderKind.Proxy,
                    Name = "provider-a",
                },
                CancellationToken.None));

        Assert.Equal(MihomoServiceIpcEndpoint.OwnerMismatchCode, logsFailure.Message);
        Assert.Equal(MihomoServiceIpcEndpoint.OwnerMismatchCode, controllerFailure.Message);
        Assert.Equal(MihomoServiceIpcEndpoint.OwnerMismatchCode, providerFailure.Message);
        Assert.Empty(runner.Requests);
        Assert.Empty(ipc.Requests);
    }

    /// <summary>Verifies an inconclusive query cannot be cached as a confirmed missing service.</summary>
    [Fact]
    public async Task GetStatusAsync_WhenScQueryTimesOut_ReturnsAndCachesUnknownStatus()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Result(ProcessRunOutcome.TimedOut));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);
        MihomoServiceStatus cachedStatus = manager.GetLatestStatus();

        Assert.False(status.IsKnown);
        Assert.Equal("unknown", status.Message);
        Assert.Equal(status, cachedStatus);
    }

    /// <summary>Verifies a running service query maps to a localized running status.</summary>
    [Fact]
    public async Task GetStatusAsync_WhenScQueryReportsRunning_ReturnsLocalizedRunningStatus()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new();
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsInstalled);
        Assert.True(status.IsScmRunning);
        Assert.True(status.IsRunning);
        Assert.True(status.HasRunningChild);
        Assert.Equal(MihomoServiceIpcProtocol.CurrentVersion, status.ProtocolVersion);
        Assert.NotNull(status.ServiceSessionId);
        Assert.Equal(MihomoServiceChildState.Running, status.ChildState);
        Assert.Equal(1, status.ActiveGeneration);
        Assert.Equal(new string('a', 64), status.ActiveConfigurationHash);
        Assert.Equal("running", status.Message);
        Assert.Equal(
            [MihomoServiceIpcCommand.Hello, MihomoServiceIpcCommand.Status],
            ipc.Requests.Select(request => request.Command));
    }

    /// <summary>Verifies query cancellation is propagated instead of being misreported as service absence.</summary>
    [Fact]
    public async Task GetStatusAsync_WhenRunnerReturnsCancellation_PropagatesCancellation()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Result(ProcessRunOutcome.Cancelled));
        MihomoServiceManager manager = CreateManager(runner);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => manager.GetStatusAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
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
        Assert.Contains("--pipe-name \"ClashSharp.Mihomo.", create.Arguments[3], StringComparison.Ordinal);
        Assert.Contains($"--ipc-token \"{IpcToken}\"", create.Arguments[3], StringComparison.Ordinal);
        Assert.DoesNotContain("--workdir", create.Arguments[3], StringComparison.Ordinal);
        Assert.Contains(
            "--allowed-sid \"S-1-5-21-100-200-300-1001\"",
            create.Arguments[3],
            StringComparison.Ordinal);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], runner.Requests[2].Arguments);
    }

    /// <summary>Verifies an untrusted LocalSystem binary path is rejected before elevation.</summary>
    [Fact]
    public async Task DeployAsync_WhenBinaryTrustValidationFails_DoesNotMutateScm()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(1060));
        FakeMihomoServiceBinaryTrustValidator validator = new(
            MihomoServiceBinaryTrustValidation.Denied(
                "service host",
                "path grants modification rights to an untrusted principal"));
        MihomoServiceManager manager = CreateManager(
            runner,
            serviceHostPath: @"C:\Users\example\service.exe",
            binaryTrustValidator: validator);

        MihomoServiceStatus status = await manager.DeployAsync(CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.False(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.Equal("untrusted binaries", status.Message);
        Assert.Equal(
            (@"C:\Users\example\service.exe", @"C:\mihomo.exe"),
            Assert.Single(validator.Requests));
        Assert.Single(runner.Requests);
        Assert.False(runner.Requests[0].RunElevated);
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
        using CancellationTokenSource cancellation = new();
        runner.OnRequest = requestCount =>
        {
            if (requestCount == 2)
            {
                cancellation.Cancel();
            }
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DeployAsync(cancellation.Token));

        Assert.Equal(3, runner.Requests.Count);
        Assert.False(runner.Requests[2].RunElevated);
        Assert.Equal(["query", MihomoServiceManager.ServiceName], runner.Requests[2].Arguments);
    }

    /// <summary>Verifies cancellation before admission cannot start any SCM process.</summary>
    [Fact]
    public async Task DeployAsync_WhenAlreadyCancelled_DoesNotRunElevatedCommand()
    {
        FakeProcessRunner runner = new() { ObserveCancellation = true };
        MihomoServiceManager manager = CreateManager(runner, serviceHostPath: @"C:\service.exe");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DeployAsync(cancellation.Token));

        Assert.Empty(runner.Requests);
    }

    /// <summary>Verifies an inconclusive initial query prevents an unsafe deployment attempt.</summary>
    [Fact]
    public async Task DeployAsync_WhenInitialQueryTimesOut_DoesNotRunElevatedCommand()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Result(ProcessRunOutcome.TimedOut));
        MihomoServiceManager manager = CreateManager(runner, serviceHostPath: @"C:\service.exe");

        MihomoServiceStatus status = await manager.DeployAsync(CancellationToken.None);

        Assert.False(status.IsKnown);
        Assert.False(status.IsInstalled);
        Assert.Equal("deployment failed", status.Message);
        Assert.Single(runner.Requests);
        Assert.False(runner.Requests[0].RunElevated);
    }

    /// <summary>Verifies a successful create command plus an inconclusive final query is not reported as confirmed absence.</summary>
    [Fact]
    public async Task DeployAsync_WhenFinalQueryTimesOut_ReturnsUnknownFailureStatus()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(1060));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Result(ProcessRunOutcome.TimedOut));
        MihomoServiceManager manager = CreateManager(runner, serviceHostPath: @"C:\service.exe");

        MihomoServiceStatus status = await manager.DeployAsync(CancellationToken.None);

        Assert.False(status.IsKnown);
        Assert.False(status.IsInstalled);
        Assert.Equal("deployment failed", status.Message);
        Assert.Equal(3, runner.Requests.Count);
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

        Assert.False(status.IsKnown);
        Assert.False(status.IsInstalled);
        Assert.Equal("removal failed", status.Message);
        Assert.Single(runner.Requests);
        Assert.False(runner.Requests[0].RunElevated);
    }

    [Fact]
    public async Task RestartAsync_WhenServiceHostIsRunning_ReloadsExactGenerationWithoutScmBounce()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new();
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.True(status.IsInstalled);
        Assert.True(status.IsRunning);
        Assert.Equal(["query"], runner.Requests.Select(request => request.Arguments[0]));
        Assert.Equal(
            [MihomoServiceIpcCommand.Hello, MihomoServiceIpcCommand.Status, MihomoServiceIpcCommand.Reload],
            ipc.Requests.Select(request => request.Command));
        MihomoServiceIpcRequest reload = ipc.Requests[2];
        Assert.Equal(Generation, reload.Generation);
        Assert.Equal(ConfigurationHash, reload.ConfigurationHash);
    }

    [Fact]
    public async Task RestartAsync_WhenServiceIsStopped_RequiresInstallerRepairWithoutElevation()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        FakeMihomoServiceIpcClient ipc = new();
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.True(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.False(status.IsScmRunning);
        Assert.Equal(RuntimeFailureDiagnostics.ServiceUnavailable, status.IpcFailureCode);
        Assert.Equal(["query"], runner.Requests.Select(request => request.Arguments[0]));
        Assert.DoesNotContain(runner.Requests, request => request.RunElevated);
        Assert.Empty(ipc.Requests);
    }

    [Fact]
    public async Task StopAsync_WhenServiceIsRunning_StopsChildAndKeepsHostAvailable()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new();
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.StopAsync(CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.True(status.IsInstalled);
        Assert.False(status.IsRunning);
        Assert.True(status.IsScmRunning);
        Assert.Equal(["query"], runner.Requests.Select(request => request.Arguments[0]));
        Assert.Equal(
            [MihomoServiceIpcCommand.Hello, MihomoServiceIpcCommand.Status, MihomoServiceIpcCommand.Stop],
            ipc.Requests.Select(request => request.Command));
        Assert.DoesNotContain(runner.Requests, request => request.RunElevated);
    }

    [Fact]
    public async Task StopAsync_WhenScmStatusIsUnknown_DoesNotMutateService()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Result(ProcessRunOutcome.TimedOut));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.StopAsync(CancellationToken.None);

        Assert.False(status.IsKnown);
        Assert.Single(runner.Requests);
        Assert.False(runner.Requests[0].RunElevated);
    }

    [Fact]
    public async Task RestartAsync_WaitsForExistingStartPendingBeforeReloading()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 2  START_PENDING"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);

        Assert.True(status.IsRunning);
        Assert.Equal(
            ["query", "query"],
            runner.Requests.Select(request => request.Arguments[0]));
        Assert.DoesNotContain(runner.Requests, request => request.RunElevated);
    }

    [Fact]
    public async Task StopAsync_WhenIpcFails_WaitsForScmStopPendingBeforeReleasingServiceOwner()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 3  STOP_PENDING"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        FakeMihomoServiceIpcClient ipc = new() { Failure = new TimeoutException("pipe timeout") };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.StopAsync(CancellationToken.None);

        Assert.False(status.IsRunning);
        Assert.Equal(
            ["query", "stop", "query", "query"],
            runner.Requests.Select(request => request.Arguments[0]));
    }

    [Fact]
    public async Task GetStatusAsync_WhenStartIsAlreadyPending_WaitsForTerminalOwnerState()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 2  START_PENDING"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.True(status.IsRunning);
        Assert.Equal(["query", "query"], runner.Requests.Select(request => request.Arguments[0]));
    }

    [Fact]
    public async Task RestartAsync_WhenStopIsAlreadyPending_WaitsThenRequiresInstallerRepair()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 3  STOP_PENDING"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        MihomoServiceManager manager = CreateManager(runner);

        MihomoServiceStatus status = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.False(status.IsRunning);
        Assert.Equal(RuntimeFailureDiagnostics.ServiceUnavailable, status.IpcFailureCode);
        Assert.Equal(
            ["query", "query"],
            runner.Requests.Select(request => request.Arguments[0]));
        Assert.DoesNotContain(runner.Requests, request => request.RunElevated);
    }

    [Fact]
    public async Task GetStatusAsync_WhenIpcTimesOut_PreservesScmObservationButFailsClosed()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new() { Failure = new TimeoutException("pipe timeout") };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.True(status.IsInstalled);
        Assert.True(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.ipc.timeout", status.IpcFailureCode);
        Assert.Equal(MihomoServiceIpcCommand.Hello, Assert.Single(ipc.Requests).Command);
    }

    [Fact]
    public async Task GetStatusAsync_WhenPipeBelongsToWrongProcess_ReportsEndpointOccupied()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new()
        {
            Failure = new MihomoServicePipeServerIdentityException(),
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.ipc.endpoint_occupied", status.IpcFailureCode);
    }

    [Fact]
    public async Task ReadHostLogsAsync_RequestsMaximumBoundedRedactedSnapshot()
    {
        FakeProcessRunner runner = new();
        FakeMihomoServiceIpcClient ipc = new()
        {
            ResponseFactory = request => CreateIpcResponse(request, Guid.NewGuid()) with
            {
                Logs = ["redacted service entry"],
            },
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        IReadOnlyList<string> logs = await manager.ReadHostLogsAsync(CancellationToken.None);

        Assert.Equal(["redacted service entry"], logs);
        MihomoServiceIpcRequest request = Assert.Single(ipc.Requests);
        Assert.Equal(MihomoServiceIpcCommand.Logs, request.Command);
        Assert.Equal(MihomoServiceIpcProtocol.MaximumLogEntries, request.MaximumLogEntries);
    }

    [Fact]
    public async Task GetStatusAsync_WhenProtocolClientRejectsVersion_PreservesScmObservationButFailsClosed()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new()
        {
            Failure = new InvalidDataException("incompatible protocol"),
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.ipc.protocol_invalid", status.IpcFailureCode);
    }

    [Fact]
    public async Task GetStatusAsync_WhenSessionChangesDuringHandshake_FailsClosed()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        Guid helloSession = Guid.NewGuid();
        Guid statusSession = Guid.NewGuid();
        int responseCount = 0;
        FakeMihomoServiceIpcClient ipc = new()
        {
            ResponseFactory = request => CreateIpcResponse(
                request,
                responseCount++ == 0 ? helloSession : statusSession),
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.ipc.protocol_invalid", status.IpcFailureCode);
        Assert.Equal(2, ipc.Requests.Count);
    }

    [Fact]
    public async Task GetStatusAsync_WhenAuthenticationIsRejected_FailsClosedWithServiceError()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new()
        {
            ResponseFactory = request => new MihomoServiceIpcResponse
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = request.RequestId,
                Succeeded = false,
                ErrorCode = "service.ipc.authentication_failed",
            },
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.GetStatusAsync(CancellationToken.None);

        Assert.True(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.ipc.authentication_failed", status.IpcFailureCode);
    }

    [Fact]
    public async Task DeployAsync_WhenOldInstallationExists_ReconcilesFullBinPathWhileStopped()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        FakeMihomoServiceIpcClient ipc = new();
        MihomoServiceManager manager = CreateManager(
            runner,
            serviceHostPath: @"C:\new service\ClashSharp.MihomoService.exe",
            ipcClient: ipc);

        MihomoServiceStatus status = await manager.DeployAsync(CancellationToken.None);

        Assert.True(status.IsInstalled);
        Assert.False(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal(
            ["query", "stop", "query", "config", "query"],
            runner.Requests.Select(request => request.Arguments[0]));
        Assert.Equal(
            [MihomoServiceIpcCommand.Hello, MihomoServiceIpcCommand.Status, MihomoServiceIpcCommand.Stop],
            ipc.Requests.Select(request => request.Command));
        ProcessRequest config = runner.Requests[3];
        Assert.True(config.RunElevated);
        Assert.Contains("\"C:\\new service\\ClashSharp.MihomoService.exe\"", config.Arguments[3], StringComparison.Ordinal);
        Assert.Contains($"--ipc-token \"{IpcToken}\"", config.Arguments[3], StringComparison.Ordinal);
        Assert.Contains("--allowed-sid \"S-1-5-21-100-200-300-1001\"", config.Arguments[3], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeployAsync_WhenExistingConfigurationUpdateFails_DoesNotClaimReady()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        runner.Results.Enqueue(Completed(5, standardError: "access denied"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        MihomoServiceManager manager = CreateManager(runner, serviceHostPath: @"C:\service.exe");

        MihomoServiceStatus status = await manager.DeployAsync(CancellationToken.None);

        Assert.True(status.IsInstalled);
        Assert.False(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("deployment failed", status.Message);
        Assert.Equal(["query", "config", "query"], runner.Requests.Select(request => request.Arguments[0]));
    }

    [Fact]
    public async Task RestartAsync_WhenReturnedHashDiffers_StopsScmAndFailsClosed()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        Guid session = Guid.NewGuid();
        FakeMihomoServiceIpcClient ipc = new()
        {
            ResponseFactory = request => CreateIpcResponse(
                request,
                session,
                generation: request.Command is MihomoServiceIpcCommand.Start or MihomoServiceIpcCommand.Reload
                    ? request.Generation
                    : 1,
                configurationHash: request.Command is MihomoServiceIpcCommand.Start or MihomoServiceIpcCommand.Reload
                    ? new string('b', 64)
                    : new string('a', 64)),
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.False(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.ipc.protocol_invalid", status.IpcFailureCode);
        Assert.Null(status.CleanupFailureCode);
        Assert.Equal(["query", "stop", "query"], runner.Requests.Select(request => request.Arguments[0]));
    }

    [Fact]
    public async Task RestartAsync_WhenCleanupCannotStopScm_ReportsCleanupFailureAndNeverReady()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(5, standardError: "access denied"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        Guid session = Guid.NewGuid();
        FakeMihomoServiceIpcClient ipc = new()
        {
            ResponseFactory = request => CreateIpcResponse(
                request,
                session,
                generation: request.Command is MihomoServiceIpcCommand.Start or MihomoServiceIpcCommand.Reload
                    ? request.Generation
                    : 1,
                configurationHash: new string('a', 64)),
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);

        Assert.True(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.ipc.scm_stop_not_confirmed", status.CleanupFailureCode);
    }

    [Fact]
    public async Task RestartAsync_WhenServiceReturnsError_StopsScmAndPreservesErrorCode()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        Guid session = Guid.NewGuid();
        FakeMihomoServiceIpcClient ipc = new()
        {
            ResponseFactory = request => request.Command == MihomoServiceIpcCommand.Reload
                ? new MihomoServiceIpcResponse
                {
                    ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                    RequestId = request.RequestId,
                    Succeeded = false,
                    ErrorCode = "service.child.start_failed",
                }
                : CreateIpcResponse(request, session),
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.RestartAsync(
            Generation,
            ConfigurationHash,
            CancellationToken.None);

        Assert.False(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal("service.child.start_failed", status.IpcFailureCode);
        Assert.Null(status.CleanupFailureCode);
    }

    [Fact]
    public async Task StopAsync_WhenIpcFails_StillStopsScmAndReleasesOnlyAfterStoppedObservation()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        FakeMihomoServiceIpcClient ipc = new() { Failure = new TimeoutException("pipe timeout") };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.StopAsync(CancellationToken.None);

        Assert.False(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal(["query", "stop", "query"], runner.Requests.Select(request => request.Arguments[0]));
        Assert.Equal(MihomoServiceIpcCommand.Hello, Assert.Single(ipc.Requests).Command);
    }

    [Fact]
    public async Task StopAsync_WhenCallerCancelsAfterIpcCommit_ConfirmsChildStopBeforePropagatingCancellation()
    {
        using CancellationTokenSource cancellation = new();
        FakeProcessRunner runner = new() { ObserveCancellation = true };
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new()
        {
            OnRequest = request =>
            {
                if (request.Command == MihomoServiceIpcCommand.Stop)
                {
                    cancellation.Cancel();
                }
            },
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.StopAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(["query"], runner.Requests.Select(request => request.Arguments[0]));
        Assert.DoesNotContain(runner.Requests, request => request.RunElevated);
        Assert.True(manager.GetLatestStatus().IsScmRunning);
        Assert.False(manager.GetLatestStatus().IsRunning);
    }

    [Fact]
    public async Task DeployAsync_WhenCallerCancelsAfterOldChildStop_CompletesScmReconciliationBeforePropagatingCancellation()
    {
        using CancellationTokenSource cancellation = new();
        FakeProcessRunner runner = new() { ObserveCancellation = true };
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        FakeMihomoServiceIpcClient ipc = new()
        {
            OnRequest = request =>
            {
                if (request.Command == MihomoServiceIpcCommand.Stop)
                {
                    cancellation.Cancel();
                }
            },
        };
        MihomoServiceManager manager = CreateManager(
            runner,
            serviceHostPath: @"C:\service.exe",
            ipcClient: ipc);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.DeployAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(
            ["query", "stop", "query", "config", "query"],
            runner.Requests.Select(request => request.Arguments[0]));
        Assert.True(runner.Requests[1].RunElevated);
        Assert.True(runner.Requests[3].RunElevated);
        Assert.True(manager.GetLatestStatus().IsInstalled);
        Assert.False(manager.GetLatestStatus().IsScmRunning);
    }

    [Fact]
    public async Task UninstallAsync_WhenCallerCancelsAfterOldChildStop_ConfirmsScmStopButDoesNotDelete()
    {
        using CancellationTokenSource cancellation = new();
        FakeProcessRunner runner = new() { ObserveCancellation = true };
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 1  STOPPED"));
        FakeMihomoServiceIpcClient ipc = new()
        {
            OnRequest = request =>
            {
                if (request.Command == MihomoServiceIpcCommand.Stop)
                {
                    cancellation.Cancel();
                }
            },
        };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        OperationCanceledException exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            manager.UninstallAsync(cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(["query", "stop", "query"], runner.Requests.Select(request => request.Arguments[0]));
        Assert.DoesNotContain(runner.Requests, request => request.Arguments[0] == "delete");
    }

    [Fact]
    public async Task StopAsync_WhenScmLeavesStopPendingAndReturnsRunning_DoesNotReleaseOwner()
    {
        FakeProcessRunner runner = new();
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        runner.Results.Enqueue(Completed(0));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 3  STOP_PENDING"));
        runner.Results.Enqueue(Completed(0, standardOutput: "STATE              : 4  RUNNING"));
        FakeMihomoServiceIpcClient ipc = new() { Failure = new TimeoutException("pipe timeout") };
        MihomoServiceManager manager = CreateManager(runner, ipcClient: ipc);

        MihomoServiceStatus status = await manager.StopAsync(CancellationToken.None);

        Assert.True(status.IsKnown);
        Assert.True(status.IsInstalled);
        Assert.True(status.IsScmRunning);
        Assert.False(status.IsReady);
        Assert.Equal(
            ["query", "stop", "query", "query"],
            runner.Requests.Select(request => request.Arguments[0]));
    }

    private static MihomoServiceIpcResponse CreateIpcResponse(
        MihomoServiceIpcRequest request,
        Guid sessionId,
        long? generation = 1,
        string? configurationHash = null)
    {
        return new MihomoServiceIpcResponse
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = request.RequestId,
            Succeeded = true,
            Snapshot = new MihomoServiceIpcSnapshot
            {
                SessionId = sessionId,
                ServiceVersion = "test-service",
                ChildState = MihomoServiceChildState.Running,
                ChildProcessId = 4242,
                ActiveGeneration = generation,
                ActiveConfigurationHash = configurationHash ?? new string('a', 64),
            },
        };
    }

    private static MihomoServiceManager CreateManager(
        FakeProcessRunner runner,
        string? serviceHostPath = null,
        FakeMihomoServiceIpcClient? ipcClient = null,
        IMihomoServiceBinaryTrustValidator? binaryTrustValidator = null,
        MihomoServiceIpcEndpoint? endpoint = null)
    {
        endpoint ??= MihomoServiceIpcEndpoint.Create(
            "S-1-5-21-100-200-300-1001",
            IpcToken);
        return new MihomoServiceManager(
            runner,
            new FakeMihomoServiceDeploymentContext(serviceHostPath),
            binaryTrustValidator ?? new FakeMihomoServiceBinaryTrustValidator(
                MihomoServiceBinaryTrustValidation.Trusted),
            endpoint,
            ipcClient ?? new FakeMihomoServiceIpcClient(),
            key => key switch
            {
                "MihomoService.Status.NotDeployed" => "not deployed",
                "MihomoService.Status.Unknown" => "unknown",
                "MihomoService.Status.DeployedRunning" => "running",
                "MihomoService.Status.Deployed" => "deployed",
                "MihomoService.Status.DeploymentFailed" => "deployment failed",
                "MihomoService.Status.UntrustedBinaries" => "untrusted binaries",
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
        public bool ObserveCancellation { get; init; }

        public Action<int>? OnRequest { get; set; }

        public Queue<ProcessRunResult> Results { get; } = [];

        public List<ProcessRequest> Requests { get; } = [];

        public Task<ProcessRunResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            OnRequest?.Invoke(Requests.Count);
            if (ObserveCancellation && cancellationToken.IsCancellationRequested)
            {
                return Task.FromResult(Result(ProcessRunOutcome.Cancelled));
            }

            return Task.FromResult(Results.Dequeue());
        }
    }

    private sealed class FakeMihomoServiceIpcClient : IMihomoServiceIpcClient
    {
        private readonly Guid _sessionId = Guid.NewGuid();

        private MihomoServiceChildState _childState = MihomoServiceChildState.Running;

        private long? _generation = 1;

        private string? _configurationHash = new('a', 64);

        public List<MihomoServiceIpcRequest> Requests { get; } = [];

        public Func<MihomoServiceIpcRequest, MihomoServiceIpcResponse>? ResponseFactory { get; init; }

        public Exception? Failure { get; init; }

        public Action<MihomoServiceIpcRequest>? OnRequest { get; init; }

        public Task<MihomoServiceIpcResponse> SendAsync(
            MihomoServiceIpcRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            OnRequest?.Invoke(request);
            if (Failure is not null)
            {
                return Task.FromException<MihomoServiceIpcResponse>(Failure);
            }

            if (ResponseFactory is not null)
            {
                return Task.FromResult(ResponseFactory(request));
            }

            switch (request.Command)
            {
                case MihomoServiceIpcCommand.Start:
                case MihomoServiceIpcCommand.Reload:
                    _childState = MihomoServiceChildState.Running;
                    _generation = request.Generation;
                    _configurationHash = request.ConfigurationHash;
                    break;
                case MihomoServiceIpcCommand.Stop:
                    _childState = MihomoServiceChildState.Stopped;
                    _generation = null;
                    _configurationHash = null;
                    break;
            }

            return Task.FromResult(CreateResponse(request));
        }

        private MihomoServiceIpcResponse CreateResponse(MihomoServiceIpcRequest request)
        {
            return new MihomoServiceIpcResponse
            {
                ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
                RequestId = request.RequestId,
                Succeeded = true,
                Snapshot = new MihomoServiceIpcSnapshot
                {
                    SessionId = _sessionId,
                    ServiceVersion = "test-service",
                    ChildState = _childState,
                    ChildProcessId = _childState == MihomoServiceChildState.Running ? 4242 : null,
                    ActiveGeneration = _generation,
                    ActiveConfigurationHash = _configurationHash,
                },
            };
        }
    }

    private sealed class FakeMihomoServiceDeploymentContext(string? serviceHostPath) : IMihomoServiceDeploymentContext
    {
        public string? ResolveServiceHostPath()
        {
            return serviceHostPath;
        }

        public string MihomoBinaryPath => @"C:\mihomo.exe";

        public CoreConfigurationState EnsureServiceConfiguration()
        {
            return new CoreConfigurationState(@"C:\mihomo", @"C:\mihomo\config.yaml", true);
        }
    }

    private sealed class FakeMihomoServiceBinaryTrustValidator(
        MihomoServiceBinaryTrustValidation result) : IMihomoServiceBinaryTrustValidator
    {
        public List<(string ServiceHostPath, string MihomoBinaryPath)> Requests { get; } = [];

        public MihomoServiceBinaryTrustValidation Validate(
            string serviceHostPath,
            string mihomoBinaryPath)
        {
            Requests.Add((serviceHostPath, mihomoBinaryPath));
            return result;
        }
    }
}
