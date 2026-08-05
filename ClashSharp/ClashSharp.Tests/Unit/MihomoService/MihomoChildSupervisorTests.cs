using System.Collections.Concurrent;
using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Verifies command-controlled immutable generation and Job-tree lifecycle semantics.</summary>
public sealed class MihomoChildSupervisorTests
{
    /// <summary>Verifies constructing the service supervisor never starts mihomo by itself.</summary>
    [Fact]
    public async Task Constructor_LeavesChildStoppedUntilStartCommand()
    {
        await using MihomoChildSupervisorTestContext context = new([]);

        MihomoServiceIpcSnapshot snapshot = context.Supervisor.GetSnapshot();

        Assert.Equal(MihomoServiceChildState.Stopped, snapshot.ChildState);
        Assert.NotEqual(Guid.Empty, snapshot.SessionId);
        Assert.Equal("1.2.3-test", snapshot.ServiceVersion);
        Assert.Empty(context.Launcher.Requests);
        Assert.Null(snapshot.Validate());
    }

    /// <summary>Verifies Start launches only the exact immutable bytes and Stop confirms tree exit.</summary>
    [Fact]
    public async Task StartAndStop_UseImmutableStagedGeneration()
    {
        FakeMihomoChildProcess process = new(
            "first",
            101,
            standardOutput: $"ready {MihomoServiceTestSupport.Token}\n",
            standardError: "warning\n");
        await using MihomoChildSupervisorTestContext context = new([process]);
        const string configuration = "mixed-port: 7891\nmode: rule\n";
        string hash = context.WriteConfiguration(configuration);

        MihomoChildOperationResult started = await context.Supervisor.StartAsync(
            7,
            hash,
            CancellationToken.None);

        Assert.True(started.Succeeded);
        Assert.Equal(MihomoServiceChildState.Running, started.Snapshot.ChildState);
        Assert.Equal(101, started.Snapshot.ChildProcessId);
        Assert.Equal(7, started.Snapshot.ActiveGeneration);
        Assert.Equal(hash, started.Snapshot.ActiveConfigurationHash);
        Assert.Null(started.Snapshot.Validate());
        MihomoChildStartRequest request = Assert.Single(context.Launcher.Requests);
        Assert.NotEqual(context.Options.ConfigPath, request.ConfigurationPath);
        Assert.StartsWith(context.Options.ServiceDataDirectory, request.ConfigurationPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(context.Options.RuntimeDirectory, request.WorkDirectory);
        Assert.StartsWith(
            context.Options.ServiceDataDirectory,
            request.WorkDirectory,
            StringComparison.OrdinalIgnoreCase);
        string sourceGenerationPath = Assert.Single(Directory.EnumerateFiles(
            context.Options.ServiceDataDirectory,
            "generation-*.yaml"));
        Assert.Equal(context.LastConfigurationText, File.ReadAllText(sourceGenerationPath));
        string effectiveText = File.ReadAllText(request.ConfigurationPath);
        Assert.Contains("external-controller-pipe", effectiveText, StringComparison.Ordinal);
        Assert.Contains("secret", effectiveText, StringComparison.Ordinal);
        Assert.True(File.GetAttributes(request.ConfigurationPath).HasFlag(FileAttributes.ReadOnly));

        File.WriteAllText(context.Options.ConfigPath, "mixed-port: 9999\n");
        Assert.Equal(context.LastConfigurationText, File.ReadAllText(sourceGenerationPath));
        await MihomoServiceTestSupport.WaitUntilAsync(() =>
            context.RuntimeLogs.ReadAfter(0, 20).Entries.Any(entry =>
                entry.Message.Contains("[redacted]", StringComparison.Ordinal)));
        Assert.DoesNotContain(
            context.Logs.ReadLatest(20),
            entry => entry.Contains("[stdout]", StringComparison.Ordinal)
                || entry.Contains("[stderr]", StringComparison.Ordinal));
        Assert.DoesNotContain(
            context.RuntimeLogs.ReadAfter(0, 20).Entries,
            entry => entry.Message.Contains(
                MihomoServiceTestSupport.Token,
                StringComparison.OrdinalIgnoreCase));

        MihomoChildOperationResult stopped = await context.Supervisor.StopAsync(
            CancellationToken.None);

        Assert.True(stopped.Succeeded);
        Assert.Equal(MihomoServiceChildState.Stopped, stopped.Snapshot.ChildState);
        Assert.Null(stopped.Snapshot.ChildProcessId);
        Assert.Null(stopped.Snapshot.ActiveGeneration);
        Assert.Null(stopped.Snapshot.ActiveConfigurationHash);
        Assert.True(process.StopCompleted);
        Assert.True(process.IsDisposed);
        Assert.False(File.Exists(request.ConfigurationPath));
        Assert.Null(stopped.Snapshot.Validate());
    }

    /// <summary>Verifies Running is not published until the internal controller probe succeeds.</summary>
    [Fact]
    public async Task Start_RemainsStartingUntilControllerReadinessSucceeds()
    {
        TaskCompletionSource<MihomoServiceIpcEffectiveConfiguration> readiness = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        FakeMihomoControllerReadinessProbe probe = new()
        {
            Handler = (_, _, _, _, _) => readiness.Task,
        };
        FakeMihomoChildProcess process = new("starting", 119);
        await using MihomoChildSupervisorTestContext context = new(
            [process],
            readinessProbe: probe);
        string hash = context.WriteConfiguration(
            "mixed-port: 7890\nmode: global\n");

        Task<MihomoChildOperationResult> start = context.Supervisor.StartAsync(
            60,
            hash,
            CancellationToken.None);
        await MihomoServiceTestSupport.WaitUntilAsync(() => probe.Calls.Count == 1);

        MihomoServiceIpcSnapshot starting = context.Supervisor.GetSnapshot();
        Assert.Equal(MihomoServiceChildState.Starting, starting.ChildState);
        Assert.Equal(119, starting.ChildProcessId);
        Assert.False(start.IsCompleted);
        FakeReadinessProbeCall call = Assert.Single(probe.Calls);
        Assert.Equal(0, call.Expected.MixedPort);
        Assert.Equal(MihomoServiceIpcRoutingMode.Global, call.Expected.Mode);
        Assert.True(call.Expected.TunEnabled);
        readiness.SetResult(new MihomoServiceIpcEffectiveConfiguration
        {
            ControllerReady = true,
            MixedPort = 0,
            Mode = MihomoServiceIpcRoutingMode.Global,
            TunEnabled = true,
        });

        MihomoChildOperationResult result = await start.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(result.Succeeded);
        Assert.Equal(MihomoServiceChildState.Running, result.Snapshot.ChildState);
    }

    /// <summary>Verifies readiness failure empties the Job before deleting the effective config.</summary>
    [Fact]
    public async Task Start_ControllerNotReady_CleansJobAndEffectiveConfiguration()
    {
        FakeMihomoControllerReadinessProbe probe = new()
        {
            Handler = (_, _, _, _, _) => Task.FromException<MihomoServiceIpcEffectiveConfiguration>(
                new MihomoControllerNotReadyException("test readiness failure")),
        };
        FakeMihomoChildProcess process = new("not-ready", 120);
        await using MihomoChildSupervisorTestContext context = new(
            [process],
            readinessProbe: probe);
        string hash = context.WriteConfiguration("mixed-port: 7890\n");

        MihomoChildOperationResult result = await context.Supervisor.StartAsync(
            61,
            hash,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("service.child.controller_not_ready", result.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Faulted, result.Snapshot.ChildState);
        Assert.Null(result.Snapshot.ChildProcessId);
        Assert.True(process.StopCompleted);
        Assert.True(process.IsDisposed);
        Assert.Empty(Directory.EnumerateFiles(
            Path.Combine(context.Options.RuntimeDirectory, "effective"),
            "effective-*.yaml"));
    }

    /// <summary>Verifies mutable source bytes cannot be activated under a false expected digest.</summary>
    [Fact]
    public async Task Start_RejectsConfigurationHashMismatchWithoutLaunching()
    {
        FakeMihomoChildProcess process = new("unused", 102);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string wrongHash = MihomoServiceTestSupport.ComputeHash("different bytes");

        MihomoChildOperationResult result = await context.Supervisor.StartAsync(
            1,
            wrongHash,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("service.child.configuration_hash_mismatch", result.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Stopped, result.Snapshot.ChildState);
        Assert.Empty(context.Launcher.Requests);
        Assert.Null(result.Snapshot.Validate());
    }

    /// <summary>Verifies one generation number cannot later be rebound to different bytes.</summary>
    [Fact]
    public async Task Start_RejectsGenerationReuseWithDifferentHash()
    {
        FakeMihomoChildProcess first = new("first", 103);
        FakeMihomoChildProcess unused = new("unused", 104);
        await using MihomoChildSupervisorTestContext context = new([first, unused]);
        string firstHash = context.WriteConfiguration("mixed-port: 7001\n");
        Assert.True((await context.Supervisor.StartAsync(
            3,
            firstHash,
            CancellationToken.None)).Succeeded);
        Assert.True((await context.Supervisor.StopAsync(CancellationToken.None)).Succeeded);
        string secondHash = context.WriteConfiguration("mixed-port: 7002\n");

        MihomoChildOperationResult result = await context.Supervisor.StartAsync(
            3,
            secondHash,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("service.child.generation_conflict", result.ErrorCode);
        Assert.Single(context.Launcher.Requests);
        Assert.Equal(MihomoServiceChildState.Stopped, result.Snapshot.ChildState);
    }

    /// <summary>Verifies Reload empties the old Job before launching the next generation.</summary>
    [Fact]
    public async Task Reload_StopsOldTreeBeforeStartingNewGeneration()
    {
        ConcurrentQueue<string> events = new();
        FakeMihomoChildProcess first = new("first", 105, events);
        FakeMihomoChildProcess second = new("second", 106, events);
        await using MihomoChildSupervisorTestContext context = new(
            [first, second],
            events: events);
        string firstHash = context.WriteConfiguration("mixed-port: 7101\n");
        Assert.True((await context.Supervisor.StartAsync(
            10,
            firstHash,
            CancellationToken.None)).Succeeded);
        string secondHash = context.WriteConfiguration("mixed-port: 7102\n");

        MihomoChildOperationResult reloaded = await context.Supervisor.ReloadAsync(
            11,
            secondHash,
            CancellationToken.None);

        Assert.True(reloaded.Succeeded);
        Assert.Equal(MihomoServiceChildState.Running, reloaded.Snapshot.ChildState);
        Assert.Equal(106, reloaded.Snapshot.ChildProcessId);
        Assert.Equal(11, reloaded.Snapshot.ActiveGeneration);
        Assert.Equal(secondHash, reloaded.Snapshot.ActiveConfigurationHash);
        Assert.True(first.StopCompleted);
        Assert.True(first.IsDisposed);
        Assert.Equal(2, context.Launcher.Requests.Count);
        string[] eventSnapshot = events.ToArray();
        Assert.True(
            Array.IndexOf(eventSnapshot, "stop-complete:first")
            < Array.IndexOf(eventSnapshot, "start:second"));
        Assert.Null(reloaded.Snapshot.Validate());
    }

    /// <summary>Verifies concurrent lifecycle commands are serialized through the Job handoff.</summary>
    [Fact]
    public async Task ReloadAndStop_AreSerializedWithoutOverlappingChildren()
    {
        ConcurrentQueue<string> events = new();
        FakeMihomoChildProcess first = new("first", 107, events, blockStop: true);
        FakeMihomoChildProcess second = new("second", 108, events);
        await using MihomoChildSupervisorTestContext context = new(
            [first, second],
            events: events);
        string firstHash = context.WriteConfiguration("mixed-port: 7201\n");
        Assert.True((await context.Supervisor.StartAsync(
            20,
            firstHash,
            CancellationToken.None)).Succeeded);
        string secondHash = context.WriteConfiguration("mixed-port: 7202\n");

        Task<MihomoChildOperationResult> reload = context.Supervisor.ReloadAsync(
            21,
            secondHash,
            CancellationToken.None);
        await first.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task<MihomoChildOperationResult> stop = context.Supervisor.StopAsync(CancellationToken.None);
        Assert.False(stop.IsCompleted);
        first.ReleaseStop();

        Assert.True((await reload.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        MihomoChildOperationResult stopped = await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Succeeded);
        Assert.Equal(MihomoServiceChildState.Stopped, stopped.Snapshot.ChildState);
        Assert.Equal(1, second.StopCalls);
        string[] eventSnapshot = events.ToArray();
        Assert.True(
            Array.IndexOf(eventSnapshot, "stop-complete:first")
            < Array.IndexOf(eventSnapshot, "start:second"));
        Assert.True(
            Array.IndexOf(eventSnapshot, "start:second")
            < Array.IndexOf(eventSnapshot, "stop-enter:second"));
    }

    /// <summary>Verifies an unexpected exit restarts only the same staged generation.</summary>
    [Fact]
    public async Task UnexpectedExit_RestartsSameImmutableGeneration()
    {
        FakeMihomoChildProcess first = new("first", 109);
        FakeMihomoChildProcess second = new("second", 110);
        await using MihomoChildSupervisorTestContext context = new(
            [first, second],
            restartBackoffs: [TimeSpan.Zero]);
        string hash = context.WriteConfiguration("mixed-port: 7301\n");
        Assert.True((await context.Supervisor.StartAsync(
            30,
            hash,
            CancellationToken.None)).Succeeded);
        string stagedPath = Assert.Single(context.Launcher.Requests).ConfigurationPath;

        first.Exit(23);
        await MihomoServiceTestSupport.WaitUntilAsync(() =>
            context.Supervisor.GetSnapshot() is
            {
                ChildState: MihomoServiceChildState.Running,
                ChildProcessId: 110,
            });

        MihomoServiceIpcSnapshot snapshot = context.Supervisor.GetSnapshot();
        Assert.Equal(30, snapshot.ActiveGeneration);
        Assert.Equal(hash, snapshot.ActiveConfigurationHash);
        Assert.Equal(2, context.Launcher.Requests.Count);
        Assert.All(context.Launcher.Requests, request =>
            Assert.Equal(stagedPath, request.ConfigurationPath));
        Assert.True(first.IsDisposed);
        Assert.Null(snapshot.Validate());
    }

    /// <summary>
    /// Verifies an observed root-process exit revokes controller readiness before a blocked broker
    /// operation releases the lifecycle command gate.
    /// </summary>
    [Fact]
    public async Task UnexpectedExit_RevokesReadyBeforeBlockedControllerOperationReleasesGate()
    {
        FakeMihomoChildProcess process = new("first", 121);
        await using MihomoChildSupervisorTestContext context = new(
            [process],
            restartBackoffs: [TimeSpan.FromSeconds(30)]);
        string hash = context.WriteConfiguration("mixed-port: 7302\n");
        MihomoChildOperationResult started = await context.Supervisor.StartAsync(
            31,
            hash,
            CancellationToken.None);
        Assert.True(started.Succeeded);
        MihomoServiceIpcControllerBinding binding = CreateBinding(started.Snapshot);
        TaskCompletionSource<object?> operationEntered = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<object?> releaseOperation = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        Task<MihomoControllerBoundOperationResult<object>> brokerOperation = context.Supervisor
            .ExecuteControllerOperationAsync<object>(
                binding,
                async (_, _) =>
                {
                    operationEntered.TrySetResult(null);
                    await releaseOperation.Task.ConfigureAwait(false);
                    return new object();
                },
                CancellationToken.None);
        await operationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        process.Exit(24);
        await MihomoServiceTestSupport.WaitUntilAsync(() =>
            context.Supervisor.GetSnapshot().FaultCode == "service.child.unexpected_exit");

        Assert.False(brokerOperation.IsCompleted);
        Assert.Equal(0, process.StopCalls);
        Assert.Equal(
            "service.controller.not_ready",
            context.Supervisor.TryGetReadyControllerContext(binding, out var controllerContext));
        Assert.Null(controllerContext);
        MihomoServiceCommandProcessor processor = new(
            context.Options,
            context.Supervisor,
            context.Logs,
            context.ControllerBroker);
        foreach (MihomoServiceIpcCommand command in
                 new[] { MihomoServiceIpcCommand.Hello, MihomoServiceIpcCommand.Status })
        {
            MihomoServiceIpcResponse response = await processor.ProcessAsync(
                CreateRequest(command),
                CancellationToken.None);
            Assert.True(response.Succeeded);
            Assert.Equal(MihomoServiceChildState.Faulted, response.Snapshot?.ChildState);
            Assert.Null(response.Snapshot?.ChildProcessId);
            Assert.Equal("service.child.unexpected_exit", response.Snapshot?.FaultCode);
            Assert.Null(response.Validate());
        }

        releaseOperation.TrySetResult(null);
        MihomoControllerBoundOperationResult<object> operationResult = await brokerOperation
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("service.controller.stale_generation", operationResult.ErrorCode);
    }

    /// <summary>Verifies an exit from an earlier lifecycle epoch cannot revoke a newer runtime.</summary>
    [Fact]
    public async Task StalePriorGenerationExit_CannotInvalidateCurrentGeneration()
    {
        FakeMihomoChildProcess first = new("first", 122)
        {
            BlockExitObservation = true,
        };
        FakeMihomoChildProcess second = new("second", 123);
        await using MihomoChildSupervisorTestContext context = new([first, second]);
        string firstHash = context.WriteConfiguration("mixed-port: 7303\n");
        MihomoChildOperationResult firstStarted = await context.Supervisor.StartAsync(
            32,
            firstHash,
            CancellationToken.None);
        Assert.True(firstStarted.Succeeded);
        MihomoServiceIpcControllerBinding firstBinding = CreateBinding(firstStarted.Snapshot);
        Assert.Null(context.Supervisor.TryGetReadyControllerContext(
            firstBinding,
            out var firstControllerContext));
        Assert.NotNull(firstControllerContext);
        await first.ExitObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        string secondHash = context.WriteConfiguration("mixed-port: 7304\n");
        MihomoChildOperationResult secondStarted = await context.Supervisor.ReloadAsync(
            33,
            secondHash,
            CancellationToken.None);
        Assert.True(secondStarted.Succeeded);
        MihomoServiceIpcControllerBinding secondBinding = CreateBinding(secondStarted.Snapshot);

        Assert.False(context.Supervisor.TryInvalidateControllerAfterUnexpectedExit(
            first,
            firstControllerContext!.LifecycleEpoch));
        first.ReleaseExitObservation();
        Assert.Null(context.Supervisor.TryGetReadyControllerContext(
            secondBinding,
            out var secondControllerContext));
        Assert.NotNull(secondControllerContext);
        MihomoServiceIpcSnapshot snapshot = context.Supervisor.GetSnapshot();
        Assert.Equal(MihomoServiceChildState.Running, snapshot.ChildState);
        Assert.Equal(123, snapshot.ChildProcessId);
        Assert.Equal(33, snapshot.ActiveGeneration);
        Assert.Equal(secondHash, snapshot.ActiveConfigurationHash);
        Assert.Null(snapshot.Validate());
    }

    /// <summary>Verifies repeated unexpected exits exhaust a bounded restart budget.</summary>
    [Fact]
    public async Task UnexpectedExit_TransitionsToFaultedAfterRestartBudget()
    {
        FakeMihomoChildProcess first = new("first", 111);
        FakeMihomoChildProcess second = new("second", 112);
        FakeMihomoChildProcess third = new("third", 113);
        await using MihomoChildSupervisorTestContext context = new(
            [first, second, third],
            restartBackoffs: [TimeSpan.Zero, TimeSpan.Zero]);
        string hash = context.WriteConfiguration("mixed-port: 7401\n");
        Assert.True((await context.Supervisor.StartAsync(
            40,
            hash,
            CancellationToken.None)).Succeeded);

        first.Exit(31);
        await WaitForRunningProcessAsync(context, 112);
        second.Exit(32);
        await WaitForRunningProcessAsync(context, 113);
        third.Exit(33);
        await MihomoServiceTestSupport.WaitUntilAsync(() =>
            context.Supervisor.GetSnapshot().FaultCode == "service.child.restart_exhausted");

        MihomoServiceIpcSnapshot snapshot = context.Supervisor.GetSnapshot();
        Assert.Equal(MihomoServiceChildState.Faulted, snapshot.ChildState);
        Assert.Null(snapshot.ChildProcessId);
        Assert.Equal(40, snapshot.ActiveGeneration);
        Assert.Equal(hash, snapshot.ActiveConfigurationHash);
        Assert.Equal(3, context.Launcher.Requests.Count);
        Assert.Null(snapshot.Validate());
    }

    /// <summary>Verifies service shutdown closes the kill-on-close owner after stop confirmation fails.</summary>
    [Fact]
    public async Task Shutdown_ForcesJobOwnerDisposalAfterStopFailure()
    {
        FakeMihomoChildProcess process = new("first", 114)
        {
            StopFailure = new TimeoutException("test stop timeout"),
        };
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7501\n");
        Assert.True((await context.Supervisor.StartAsync(
            50,
            hash,
            CancellationToken.None)).Succeeded);

        Task firstShutdown = context.Supervisor.ShutdownAsync();
        Task secondShutdown = context.Supervisor.ShutdownAsync();
        Assert.Same(firstShutdown, secondShutdown);
        await firstShutdown.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, process.StopCalls);
        Assert.True(process.IsDisposed);
        Assert.True(process.HasExited);
    }

    /// <summary>Verifies shutdown owns and drains the exit observer even when it ignores cancellation.</summary>
    [Fact]
    public async Task Shutdown_WaitsForTrackedExitMonitor()
    {
        FakeMihomoChildProcess process = new("first", 117)
        {
            BlockExitObservation = true,
        };
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7502\n");
        Assert.True((await context.Supervisor.StartAsync(
            53,
            hash,
            CancellationToken.None)).Succeeded);
        await process.ExitObservationEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task shutdown = context.Supervisor.ShutdownAsync();
        await MihomoServiceTestSupport.WaitUntilAsync(() => process.IsDisposed);

        Assert.False(shutdown.IsCompleted);
        process.ReleaseExitObservation();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>Verifies shutdown drains output pumps and observes their unexpected exceptions.</summary>
    [Fact]
    public async Task Shutdown_WaitsForTrackedOutputPumpAndObservesFailure()
    {
        GatedTextReader output = new(new InvalidOperationException("test output failure"));
        FakeMihomoChildProcess process = new(
            "first",
            118,
            standardOutputReader: output);
        await using MihomoChildSupervisorTestContext context = new([process]);
        string hash = context.WriteConfiguration("mixed-port: 7503\n");
        Assert.True((await context.Supervisor.StartAsync(
            54,
            hash,
            CancellationToken.None)).Succeeded);
        await output.ReadEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Task shutdown = context.Supervisor.ShutdownAsync();
        await MihomoServiceTestSupport.WaitUntilAsync(() => process.IsDisposed);

        Assert.False(shutdown.IsCompleted);
        output.Release();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Contains(
            context.Logs.ReadLatest(20),
            entry => entry.Contains(
                "Background operation stdout failed (InvalidOperationException).",
                StringComparison.Ordinal));
    }

    /// <summary>Verifies failed launch cleanup retains ownership until a later command confirms Job exit.</summary>
    [Fact]
    public async Task LaunchCleanupFailure_BlocksStartAndReloadRetriesOwnedJobBeforeHandoff()
    {
        FakeMihomoChildProcess first = new("first", 115)
        {
            OutputReadFailure = new InvalidOperationException("test output setup failure"),
            StopFailure = new TimeoutException("test cleanup timeout"),
        };
        FakeMihomoChildProcess second = new("second", 116);
        await using MihomoChildSupervisorTestContext context = new([first, second]);
        string firstHash = context.WriteConfiguration("mixed-port: 7511\n");

        MihomoChildOperationResult failed = await context.Supervisor.StartAsync(
            51,
            firstHash,
            CancellationToken.None);
        MihomoChildOperationResult blocked = await context.Supervisor.StartAsync(
            51,
            firstHash,
            CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.Equal("service.child.launch_cleanup_failed", failed.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Faulted, failed.Snapshot.ChildState);
        Assert.Equal(115, failed.Snapshot.ChildProcessId);
        Assert.False(first.IsDisposed);
        Assert.False(blocked.Succeeded);
        Assert.Equal("service.child.already_running", blocked.ErrorCode);
        Assert.Single(context.Launcher.Requests);

        first.StopFailure = null;
        string secondHash = context.WriteConfiguration("mixed-port: 7512\n");
        MihomoChildOperationResult reloaded = await context.Supervisor.ReloadAsync(
            52,
            secondHash,
            CancellationToken.None);

        Assert.True(reloaded.Succeeded);
        Assert.True(first.StopCompleted);
        Assert.True(first.IsDisposed);
        Assert.Equal(116, reloaded.Snapshot.ChildProcessId);
        Assert.Equal(52, reloaded.Snapshot.ActiveGeneration);
        Assert.Equal(2, context.Launcher.Requests.Count);
        Assert.Null(reloaded.Snapshot.Validate());
    }

    private static Task WaitForRunningProcessAsync(
        MihomoChildSupervisorTestContext context,
        int processId)
    {
        return MihomoServiceTestSupport.WaitUntilAsync(() =>
            context.Supervisor.GetSnapshot() is
            {
                ChildState: MihomoServiceChildState.Running,
                ChildProcessId: int currentProcessId,
            }
            && currentProcessId == processId);
    }

    private static MihomoServiceIpcControllerBinding CreateBinding(
        MihomoServiceIpcSnapshot snapshot)
    {
        return new MihomoServiceIpcControllerBinding
        {
            ServiceSessionId = snapshot.SessionId,
            Generation = snapshot.ActiveGeneration!.Value,
            ConfigurationHash = snapshot.ActiveConfigurationHash!,
        };
    }

    private static MihomoServiceIpcRequest CreateRequest(MihomoServiceIpcCommand command)
    {
        return new MihomoServiceIpcRequest
        {
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            RequestId = Guid.NewGuid(),
            AuthenticationToken = MihomoServiceTestSupport.Token,
            Command = command,
        };
    }
}
