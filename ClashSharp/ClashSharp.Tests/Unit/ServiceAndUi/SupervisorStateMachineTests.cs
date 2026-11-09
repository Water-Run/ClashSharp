using ClashSharp.MihomoService;
using ClashSharp.ServiceProtocol;
using ClashSharp.Tests.Unit.MihomoService;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// State-machine contract of the service child supervisor: start, crash, bounded backoff, budget
/// exhaustion, explicit recovery, stop/reload/shutdown racing a start or a pending restart.
/// </summary>
public sealed class SupervisorStateMachineTests
{
    private enum Phase
    {
        Stopped,
        Running,
        FaultedNoChild,
        FaultedOwned,
    }

    private enum Operation
    {
        Start,
        Reload,
        Stop,
        Crash,
        StartWithLaunchFailure,
        StartWithReadinessFailure,
        ReloadWithLaunchFailure,
        StopWithTerminationFailure,
        ReloadWithTerminationFailure,
    }

    /// <summary>Gets one row per seeded random schedule.</summary>
    public static TheoryData<int> Seeds
    {
        get
        {
            TheoryData<int> data = [];
            for (int seed = 0; seed < 6; seed++)
            {
                data.Add(seed);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public async Task RandomCommandSchedules_KeepSnapshotChildAndFileInvariants_AndAlwaysAllowRecovery(int seed)
    {
        const int budget = 2;
        Random random = new(20261109 + seed);
        await using SupervisorHarness harness = new([TimeSpan.Zero, TimeSpan.Zero]);
        Phase phase = Phase.Stopped;
        int crashes = 0;
        bool exhausted = false;
        long generation = 0;
        Operation[] operations = Enum.GetValues<Operation>();

        for (int step = 0; step < 45; step++)
        {
            Operation operation = operations[random.Next(operations.Length)];
            FakeMihomoChildProcess? owned = phase is Phase.Running or Phase.FaultedOwned
                ? harness.Launcher.Snapshot().Single(process => !process.HasExited)
                : null;
            string label = $"seed {seed} step {step} {operation} from {phase}";
            MihomoChildOperationResult? result = null;
            if (owned is not null)
            {
                // A termination failure lasts for exactly the operation that armed it.
                owned.StopFailure = null;
            }

            switch (operation)
            {
                case Operation.Start:
                case Operation.StartWithLaunchFailure:
                case Operation.StartWithReadinessFailure:
                    {
                        harness.Launcher.FailNextLaunch = operation == Operation.StartWithLaunchFailure;
                        if (operation == Operation.StartWithReadinessFailure)
                        {
                            harness.MakeReadinessFail();
                        }

                        string hash = harness.WriteNewConfiguration();
                        result = await harness.Supervisor.StartAsync(++generation, hash, CancellationToken.None);
                        if (phase is Phase.Running or Phase.FaultedOwned)
                        {
                            Assert.False(result.Succeeded, label);
                            Assert.Equal("service.child.already_running", result.ErrorCode);
                        }
                        else if (operation == Operation.StartWithLaunchFailure)
                        {
                            Assert.Equal("service.child.launch_failed", result.ErrorCode);
                            phase = Phase.FaultedNoChild;
                            exhausted = false;
                        }
                        else if (operation == Operation.StartWithReadinessFailure)
                        {
                            Assert.Equal("service.child.controller_not_ready", result.ErrorCode);
                            phase = Phase.FaultedNoChild;
                            exhausted = false;
                        }
                        else
                        {
                            Assert.True(result.Succeeded, $"{label}: {result.ErrorCode}");
                            phase = Phase.Running;
                            crashes = 0;
                            exhausted = false;
                        }

                        break;
                    }

                case Operation.Reload:
                case Operation.ReloadWithLaunchFailure:
                case Operation.ReloadWithTerminationFailure:
                    {
                        harness.Launcher.FailNextLaunch = operation == Operation.ReloadWithLaunchFailure;
                        bool terminationFails = operation == Operation.ReloadWithTerminationFailure && owned is not null;
                        if (terminationFails)
                        {
                            owned!.StopFailure = new TimeoutException("scripted termination failure");
                        }

                        string hash = harness.WriteNewConfiguration();
                        result = await harness.Supervisor.ReloadAsync(++generation, hash, CancellationToken.None);
                        if (terminationFails)
                        {
                            Assert.Equal("service.child.stop_failed", result.ErrorCode);
                            phase = Phase.FaultedOwned;
                            break;
                        }

                        // A previously failed termination is retried (and now succeeds) by reload.
                        if (operation == Operation.ReloadWithLaunchFailure)
                        {
                            Assert.Equal("service.child.launch_failed", result.ErrorCode);
                            phase = Phase.FaultedNoChild;
                        }
                        else
                        {
                            Assert.True(result.Succeeded, $"{label}: {result.ErrorCode}");
                            phase = Phase.Running;
                        }

                        crashes = 0;
                        exhausted = false;
                        break;
                    }

                case Operation.Stop:
                case Operation.StopWithTerminationFailure:
                    {
                        bool terminationFails = operation == Operation.StopWithTerminationFailure && owned is not null;
                        if (terminationFails)
                        {
                            owned!.StopFailure = new TimeoutException("scripted termination failure");
                        }

                        result = await harness.Supervisor.StopAsync(CancellationToken.None);
                        if (terminationFails)
                        {
                            Assert.Equal("service.child.stop_failed", result.ErrorCode);
                            phase = Phase.FaultedOwned;
                        }
                        else
                        {
                            Assert.True(result.Succeeded, $"{label}: {result.ErrorCode}");
                            phase = Phase.Stopped;
                            exhausted = false;
                        }

                        break;
                    }

                case Operation.Crash:
                    {
                        if (phase != Phase.Running)
                        {
                            break;
                        }

                        int startedBefore = harness.Launcher.Snapshot().Length;
                        owned!.Exit(1);
                        crashes++;
                        if (crashes <= budget)
                        {
                            await SupervisorHarness.WaitUntilAsync(() =>
                                harness.Launcher.Snapshot().Length == startedBefore + 1
                                && harness.Supervisor.GetSnapshot().ChildState == MihomoServiceChildState.Running);
                        }
                        else
                        {
                            await SupervisorHarness.WaitUntilAsync(() =>
                                harness.Supervisor.GetSnapshot().FaultCode == "service.child.restart_exhausted");
                            phase = Phase.FaultedNoChild;
                            exhausted = true;
                        }

                        break;
                    }
            }

            harness.Launcher.FailNextLaunch = false;
            harness.MakeReadinessSucceed();
            AssertInvariants(harness, phase, exhausted, label, result);
        }

        // Explicit recovery: whatever happened, a clean stop and a fresh start must work.
        foreach (FakeMihomoChildProcess process in harness.Launcher.Snapshot())
        {
            process.StopFailure = null;
        }

        Assert.True((await harness.Supervisor.StopAsync(CancellationToken.None)).Succeeded);
        MihomoChildOperationResult recovered = await harness.Supervisor.StartAsync(
            ++generation,
            harness.WriteNewConfiguration(),
            CancellationToken.None);
        Assert.True(recovered.Succeeded, recovered.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Running, recovered.Snapshot.ChildState);
        Assert.True((await harness.Supervisor.StopAsync(CancellationToken.None)).Succeeded);
        Assert.Empty(harness.EffectiveFiles());
        Assert.All(harness.Launcher.Snapshot(), process => Assert.True(process.IsDisposed));
        Assert.False(harness.Launcher.OverlapDetected, "Two mihomo children were alive at the same time.");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public async Task RestartBudget_EqualsTheBackoffCount_ThenFaultsAndOneExplicitReloadRearmsIt(int budget)
    {
        await using SupervisorHarness harness = new([.. Enumerable.Repeat(TimeSpan.Zero, budget)]);
        MihomoChildOperationResult started = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        Assert.True(started.Succeeded);

        for (int crash = 1; crash <= budget; crash++)
        {
            int before = harness.Launcher.Snapshot().Length;
            harness.Launcher.Snapshot()[^1].Exit(100 + crash);
            await SupervisorHarness.WaitUntilAsync(() =>
                harness.Launcher.Snapshot().Length == before + 1
                && harness.Supervisor.GetSnapshot().ChildState == MihomoServiceChildState.Running);
        }

        harness.Launcher.Snapshot()[^1].Exit(999);
        await SupervisorHarness.WaitUntilAsync(() =>
            harness.Supervisor.GetSnapshot().FaultCode == "service.child.restart_exhausted");

        MihomoServiceIpcSnapshot faulted = harness.Supervisor.GetSnapshot();
        Assert.Equal(budget + 1, harness.Launcher.Attempts);
        Assert.Equal(MihomoServiceChildState.Faulted, faulted.ChildState);
        Assert.Null(faulted.ChildProcessId);
        Assert.NotNull(faulted.ActiveGeneration);
        Assert.Null(faulted.Validate());
        Assert.All(harness.Launcher.Snapshot(), process => Assert.True(process.IsDisposed));

        // Explicit recovery re-arms the whole budget.
        MihomoChildOperationResult reloaded = await harness.Supervisor.ReloadAsync(
            2,
            harness.Hash,
            CancellationToken.None);
        Assert.True(reloaded.Succeeded, reloaded.ErrorCode);
        int attemptsAfterReload = harness.Launcher.Attempts;
        for (int crash = 1; crash <= budget; crash++)
        {
            int before = harness.Launcher.Snapshot().Length;
            harness.Launcher.Snapshot()[^1].Exit(200 + crash);
            await SupervisorHarness.WaitUntilAsync(() =>
                harness.Launcher.Snapshot().Length == before + 1
                && harness.Supervisor.GetSnapshot().ChildState == MihomoServiceChildState.Running);
        }

        Assert.Equal(attemptsAfterReload + budget, harness.Launcher.Attempts);
        Assert.False(harness.Launcher.OverlapDetected);
    }

    [Fact]
    public async Task RestartLaunchFailures_ConsumeTheBudgetAndNeverLoopForever()
    {
        await using SupervisorHarness harness = new([TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero]);
        Assert.True((await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None)).Succeeded);
        harness.Launcher.FailAllLaunches = true;

        harness.Launcher.Snapshot()[^1].Exit(1);
        await SupervisorHarness.WaitUntilAsync(() =>
            harness.Supervisor.GetSnapshot().FaultCode == "service.child.restart_exhausted");

        // One initial launch plus exactly one launch attempt per backoff entry.
        Assert.Equal(1 + 3, harness.Launcher.Attempts);
        Assert.Equal(MihomoServiceChildState.Faulted, harness.Supervisor.GetSnapshot().ChildState);
    }

    [Fact]
    public async Task StopDuringBackoff_CancelsThePendingRestart()
    {
        await using SupervisorHarness harness = new([TimeSpan.FromMinutes(1)]);
        Assert.True((await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None)).Succeeded);

        harness.Launcher.Snapshot()[^1].Exit(1);
        await SupervisorHarness.WaitUntilAsync(() =>
            harness.Supervisor.GetSnapshot() is { ChildState: MihomoServiceChildState.Starting, ChildProcessId: null });
        MihomoServiceIpcSnapshot waiting = harness.Supervisor.GetSnapshot();
        Assert.Null(waiting.Validate());

        MihomoChildOperationResult stopped = await harness.Supervisor.StopAsync(CancellationToken.None);

        Assert.True(stopped.Succeeded);
        Assert.Equal(MihomoServiceChildState.Stopped, stopped.Snapshot.ChildState);
        Assert.Equal(1, harness.Launcher.Attempts);
        // The cancelled restart task must be gone: shutdown drains tracked background work.
        await harness.Supervisor.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, harness.Launcher.Attempts);
        Assert.Empty(harness.EffectiveFiles());
    }

    [Fact]
    public async Task ReloadDuringBackoff_ReplacesThePendingRestartWithExactlyOneLaunch()
    {
        await using SupervisorHarness harness = new([TimeSpan.FromMinutes(1)]);
        Assert.True((await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None)).Succeeded);
        harness.Launcher.Snapshot()[^1].Exit(1);
        await SupervisorHarness.WaitUntilAsync(() =>
            harness.Supervisor.GetSnapshot() is { ChildState: MihomoServiceChildState.Starting, ChildProcessId: null });

        MihomoChildOperationResult reloaded = await harness.Supervisor.ReloadAsync(
            2,
            harness.WriteNewConfiguration(),
            CancellationToken.None);

        Assert.True(reloaded.Succeeded);
        Assert.Equal(2, harness.Launcher.Attempts);
        Assert.Equal(2, reloaded.Snapshot.ActiveGeneration);
        await harness.Supervisor.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, harness.Launcher.Attempts);
        Assert.False(harness.Launcher.OverlapDetected);
    }

    [Fact]
    public async Task ShutdownDuringBackoff_CompletesPromptlyAndNeverRestarts()
    {
        await using SupervisorHarness harness = new([TimeSpan.FromMinutes(1)]);
        Assert.True((await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None)).Succeeded);
        harness.Launcher.Snapshot()[^1].Exit(1);
        await SupervisorHarness.WaitUntilAsync(() =>
            harness.Supervisor.GetSnapshot() is { ChildState: MihomoServiceChildState.Starting, ChildProcessId: null });

        await harness.Supervisor.ShutdownAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, harness.Launcher.Attempts);
        Assert.Equal(MihomoServiceChildState.Stopped, harness.Supervisor.GetSnapshot().ChildState);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            harness.Supervisor.StartAsync(2, harness.Hash, CancellationToken.None));
    }

    [Fact]
    public async Task StopQueuedBehindASlowStart_RunsAfterTheStartCompletes()
    {
        TaskCompletionSource<MihomoServiceIpcEffectiveConfiguration> readiness = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using SupervisorHarness harness = new();
        harness.Probe.Handler = (_, _, _, _, _) => readiness.Task;
        Task<MihomoChildOperationResult> start = harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        await SupervisorHarness.WaitUntilAsync(() => harness.Probe.Calls.Count == 1);

        Task<MihomoChildOperationResult> stop = harness.Supervisor.StopAsync(CancellationToken.None);

        Assert.False(stop.IsCompleted);
        Assert.Equal(MihomoServiceChildState.Starting, harness.Supervisor.GetSnapshot().ChildState);
        readiness.SetResult(new MihomoServiceIpcEffectiveConfiguration
        {
            ControllerReady = true,
            MixedPort = 0,
            Mode = MihomoServiceIpcRoutingMode.Rule,
            TunEnabled = true,
        });
        Assert.True((await start.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        MihomoChildOperationResult stopped = await stop.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(stopped.Succeeded);
        Assert.Equal(MihomoServiceChildState.Stopped, stopped.Snapshot.ChildState);
        Assert.Equal(1, harness.Launcher.Snapshot().Single().StopCalls);
        Assert.Empty(harness.EffectiveFiles());
    }

    [Fact]
    public async Task ShutdownDuringReadiness_AbortsTheStartAndClosesTheChild()
    {
        await using SupervisorHarness harness = new();
        harness.Probe.Handler = async (_, _, _, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return new MihomoServiceIpcEffectiveConfiguration();
        };
        Task<MihomoChildOperationResult> start = harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        await SupervisorHarness.WaitUntilAsync(() => harness.Probe.Calls.Count == 1);

        Task shutdown = harness.Supervisor.ShutdownAsync();

        MihomoChildOperationResult result = await start.WaitAsync(TimeSpan.FromSeconds(5));
        await shutdown.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(result.Succeeded);
        Assert.Equal("service.child.service_stopping", result.ErrorCode);
        FakeMihomoChildProcess child = harness.Launcher.Snapshot().Single();
        Assert.True(child.IsDisposed);
        Assert.True(child.HasExited);
        Assert.Equal(MihomoServiceChildState.Stopped, harness.Supervisor.GetSnapshot().ChildState);
        Assert.Empty(harness.EffectiveFiles());
    }

    [Fact]
    public async Task ChildThatExitsDuringStartupObservation_IsFaultedCleanedAndRecoverable()
    {
        await using SupervisorHarness harness = new();
        harness.Launcher.ExitNextLaunchImmediately = true;

        MihomoChildOperationResult failed = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);

        Assert.False(failed.Succeeded);
        Assert.Equal("service.child.startup_exit", failed.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Faulted, failed.Snapshot.ChildState);
        Assert.Null(failed.Snapshot.ChildProcessId);
        Assert.Null(failed.Snapshot.Validate());
        Assert.Empty(harness.Probe.Calls);
        Assert.True(harness.Launcher.Snapshot().Single().IsDisposed);
        Assert.Empty(harness.EffectiveFiles());

        MihomoChildOperationResult recovered = await harness.Supervisor.StartAsync(2, harness.Hash, CancellationToken.None);
        Assert.True(recovered.Succeeded);
    }

    /// <summary>Gets every way a readiness projection can disagree with the launched generation.</summary>
    public static TheoryData<string> ReadinessMismatches
    {
        get
        {
            TheoryData<string> data = [];
            data.Add("not-ready");
            data.Add("mixed-port-nonzero");
            data.Add("mixed-port-negative");
            data.Add("mixed-port-too-large");
            data.Add("tun-flipped");
            foreach (MihomoServiceIpcRoutingMode mode in Enum.GetValues<MihomoServiceIpcRoutingMode>())
            {
                data.Add("mode-" + mode);
            }

            data.Add("mode-undefined");
            return data;
        }
    }

    [Theory]
    [MemberData(nameof(ReadinessMismatches))]
    public async Task ReadinessProjectionThatDisagreesWithThePlan_FaultsWithCleanupAndRecovers(string mismatch)
    {
        await using SupervisorHarness harness = new();
        harness.Probe.Handler = (_, _, expected, _, _) =>
        {
            MihomoServiceIpcEffectiveConfiguration ready = new()
            {
                ControllerReady = mismatch != "not-ready",
                MixedPort = expected.MixedPort,
                Mode = expected.Mode,
                TunEnabled = expected.TunEnabled,
            };
            ready = mismatch switch
            {
                "mixed-port-nonzero" => ready with { MixedPort = 7890 },
                "mixed-port-negative" => ready with { MixedPort = -1 },
                "mixed-port-too-large" => ready with { MixedPort = 70000 },
                "tun-flipped" => ready with { TunEnabled = !expected.TunEnabled },
                "mode-undefined" => ready with { Mode = (MihomoServiceIpcRoutingMode)99 },
                _ when mismatch.StartsWith("mode-", StringComparison.Ordinal) => ready with
                {
                    Mode = Enum.Parse<MihomoServiceIpcRoutingMode>(mismatch["mode-".Length..]),
                },
                _ => ready,
            };
            return Task.FromResult(ready);
        };

        MihomoChildOperationResult result = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);

        // The plan for the default managed configuration is mode rule, so mode-Rule is the one
        // projection that agrees; every other row must be refused.
        bool agrees = mismatch == "mode-Rule";
        if (agrees)
        {
            Assert.True(result.Succeeded);
            return;
        }

        Assert.False(result.Succeeded);
        Assert.Equal("service.child.controller_not_ready", result.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Faulted, result.Snapshot.ChildState);
        Assert.Null(result.Snapshot.ChildProcessId);
        Assert.True(harness.Launcher.Snapshot().Single().IsDisposed);
        Assert.Empty(harness.EffectiveFiles());
        harness.MakeReadinessSucceed();
        Assert.True((await harness.Supervisor.StartAsync(2, harness.Hash, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task MissingMihomoBinary_FaultsWithoutLaunchingAndRecoversWhenRestored()
    {
        await using SupervisorHarness harness = new();
        File.Delete(harness.Options.MihomoPath);

        MihomoChildOperationResult failed = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);

        Assert.Equal("service.child.binary_missing", failed.ErrorCode);
        Assert.Equal(MihomoServiceChildState.Faulted, failed.Snapshot.ChildState);
        Assert.Equal(0, harness.Launcher.Attempts);
        Assert.Empty(harness.EffectiveFiles());
        File.WriteAllText(harness.Options.MihomoPath, "restored");
        Assert.True((await harness.Supervisor.StartAsync(2, harness.Hash, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task PreCancelledCommands_ThrowAndLeaveNoTraceOfTheOperation()
    {
        await using SupervisorHarness harness = new();
        using CancellationTokenSource cancelled = new();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Supervisor.StartAsync(1, harness.Hash, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Supervisor.ReloadAsync(1, harness.Hash, cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            harness.Supervisor.StopAsync(cancelled.Token));

        Assert.Equal(0, harness.Launcher.Attempts);
        Assert.Equal(MihomoServiceChildState.Stopped, harness.Supervisor.GetSnapshot().ChildState);
        Assert.Empty(harness.EffectiveFiles());
        Assert.Empty(harness.StagedGenerationFiles());
        Assert.True((await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None)).Succeeded);
    }

    [Fact]
    public async Task CommandCancelledWhileQueuedBehindAnotherCommand_NeverRunsLater()
    {
        TaskCompletionSource<MihomoServiceIpcEffectiveConfiguration> readiness = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await using SupervisorHarness harness = new();
        harness.Probe.Handler = (_, _, _, _, _) => readiness.Task;
        Task<MihomoChildOperationResult> first = harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        await SupervisorHarness.WaitUntilAsync(() => harness.Probe.Calls.Count == 1);
        using CancellationTokenSource queuedCancellation = new();
        Task<MihomoChildOperationResult> queuedStop = harness.Supervisor.StopAsync(queuedCancellation.Token);
        Task<MihomoChildOperationResult> queuedReload = harness.Supervisor.ReloadAsync(2, harness.Hash, queuedCancellation.Token);

        queuedCancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedStop);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queuedReload);
        readiness.SetResult(new MihomoServiceIpcEffectiveConfiguration
        {
            ControllerReady = true,
            MixedPort = 0,
            Mode = MihomoServiceIpcRoutingMode.Rule,
            TunEnabled = true,
        });

        Assert.True((await first.WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
        Assert.Equal(MihomoServiceChildState.Running, harness.Supervisor.GetSnapshot().ChildState);
        Assert.Equal(1, harness.Launcher.Attempts);
        Assert.Equal(0, harness.Launcher.Snapshot().Single().StopCalls);
    }

    [Fact]
    public async Task ControllerOperation_NeverLeaksTheCommandGate_WhateverItThrows()
    {
        await using SupervisorHarness harness = new();
        MihomoChildOperationResult started = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        MihomoServiceIpcControllerBinding binding = harness.BindingOf(started.Snapshot);
        Exception[] failures =
        [
            new IOException("io"),
            new UnauthorizedAccessException("access"),
            new InvalidOperationException("state"),
            new HttpRequestException("http"),
            new System.Text.Json.JsonException("json"),
            new FormatException("format"),
            new TimeoutException("timeout"),
            new ArgumentException("argument"),
            new NotSupportedException("unsupported"),
            new OperationCanceledException("not caller cancelled"),
        ];

        foreach (Exception failure in failures)
        {
            Exception? escaped = await Record.ExceptionAsync(() => harness.Supervisor
                .ExecuteControllerOperationAsync<object>(
                    binding,
                    (_, _) => throw failure,
                    CancellationToken.None));

            // Expected transport-class failures are reported in the result; anything else propagates
            // to the broker's own catch, but never with the gate held.
            _ = escaped;
            MihomoChildOperationResult probe = await harness.Supervisor
                .StartAsync(1, harness.Hash, CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(probe.Succeeded, $"The command gate stayed held after {failure.GetType().Name}.");
        }

        using CancellationTokenSource cancellation = new();
        Task<MihomoControllerBoundOperationResult<object>> cancelled = harness.Supervisor
            .ExecuteControllerOperationAsync<object>(
                binding,
                async (_, token) =>
                {
                    cancellation.Cancel();
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                    return new object();
                },
                cancellation.Token);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.True((await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5))).Succeeded);
    }

    /// <summary>Gets one row per field of the exact runtime binding.</summary>
    public static TheoryData<string> BindingFields => ["session", "generation", "hash", "data-generation"];

    [Theory]
    [MemberData(nameof(BindingFields))]
    public async Task ControllerBinding_AnySingleFieldMismatch_IsStaleAndNeverResolves(string field)
    {
        await using SupervisorHarness harness = new();
        MihomoChildOperationResult started = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        MihomoServiceIpcControllerBinding exact = harness.BindingOf(started.Snapshot);
        Assert.Null(harness.Supervisor.TryGetReadyControllerContext(exact, out MihomoControllerRuntimeContext? context));
        Assert.NotNull(context);
        MihomoServiceIpcControllerBinding wrong = field switch
        {
            "session" => exact with { ServiceSessionId = Guid.NewGuid() },
            "generation" => exact with { Generation = exact.Generation + 1 },
            "hash" => exact with { ConfigurationHash = new string('0', 64) },
            _ => exact with { DataGenerationId = Guid.NewGuid() },
        };

        string? error = harness.Supervisor.TryGetReadyControllerContext(wrong, out MihomoControllerRuntimeContext? none);

        Assert.Equal("service.controller.stale_generation", error);
        Assert.Null(none);
        MihomoControllerBoundOperationResult<object> operation = await harness.Supervisor.ExecuteControllerOperationAsync<object>(
            wrong,
            (_, _) => throw new Xunit.Sdk.XunitException("A stale binding must never reach its operation."),
            CancellationToken.None);
        Assert.Equal("service.controller.stale_generation", operation.ErrorCode);
    }

    /// <summary>Gets every lifecycle transition that must revoke an issued controller context.</summary>
    public static TheoryData<string> RevokingTransitions => ["stop", "crash", "reload", "shutdown"];

    [Theory]
    [MemberData(nameof(RevokingTransitions))]
    public async Task ControllerContext_IsRevokedByEveryTransitionAwayFromTheObservedChild(string transition)
    {
        await using SupervisorHarness harness = new([TimeSpan.FromMinutes(1)]);
        MihomoChildOperationResult started = await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None);
        MihomoServiceIpcControllerBinding binding = harness.BindingOf(started.Snapshot);
        Assert.Null(harness.Supervisor.TryGetReadyControllerContext(binding, out MihomoControllerRuntimeContext? context));
        Assert.True(harness.Supervisor.IsControllerContextCurrent(context!));

        switch (transition)
        {
            case "stop":
                _ = await harness.Supervisor.StopAsync(CancellationToken.None);
                break;
            case "crash":
                harness.Launcher.Snapshot()[^1].Exit(3);
                await SupervisorHarness.WaitUntilAsync(() =>
                    harness.Supervisor.GetSnapshot().ChildState != MihomoServiceChildState.Running);
                break;
            case "reload":
                _ = await harness.Supervisor.ReloadAsync(2, harness.WriteNewConfiguration(), CancellationToken.None);
                break;
            default:
                await harness.Supervisor.ShutdownAsync();
                break;
        }

        Assert.False(harness.Supervisor.IsControllerContextCurrent(context!));
        Assert.NotNull(harness.Supervisor.TryGetReadyControllerContext(binding, out MihomoControllerRuntimeContext? after));
        Assert.Null(after);
        MihomoControllerBoundOperationResult<object> operation = await harness.Supervisor
            .ExecuteControllerOperationAsync<object>(
                binding,
                (_, _) => throw new Xunit.Sdk.XunitException("A revoked context must never reach its operation."),
                CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(operation.ErrorCode);
    }

    [Fact]
    public async Task ConcurrentCommands_AreSerializedSoChildrenNeverOverlap()
    {
        await using SupervisorHarness harness = new();
        Random random = new(20261109);
        List<Task<MihomoChildOperationResult>> pending = [];
        long generation = 0;

        for (int index = 0; index < 40; index++)
        {
            switch (random.Next(3))
            {
                case 0:
                    pending.Add(Task.Run(() => harness.Supervisor.StartAsync(
                        Interlocked.Increment(ref generation), harness.Hash, CancellationToken.None)));
                    break;
                case 1:
                    pending.Add(Task.Run(() => harness.Supervisor.ReloadAsync(
                        Interlocked.Increment(ref generation), harness.Hash, CancellationToken.None)));
                    break;
                default:
                    pending.Add(Task.Run(() => harness.Supervisor.StopAsync(CancellationToken.None)));
                    break;
            }
        }

        MihomoChildOperationResult[] results = await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.False(harness.Launcher.OverlapDetected, "Two mihomo children were alive at the same time.");
        Assert.All(results, result =>
        {
            Assert.Null(result.Snapshot.Validate());
            Assert.True(
                result.Succeeded || result.ErrorCode is "service.child.already_running"
                    or "service.child.generation_conflict",
                $"Unexpected failure {result.ErrorCode}");
        });
        Assert.True(harness.Launcher.Snapshot().Count(process => !process.HasExited) <= 1);
        Assert.True((await harness.Supervisor.StopAsync(CancellationToken.None)).Succeeded);
        Assert.All(harness.Launcher.Snapshot(), process => Assert.True(process.IsDisposed));
        Assert.Empty(harness.EffectiveFiles());
    }

    [Fact]
    public async Task DisposeIsIdempotentAndLeavesNoOwnedChildOrEffectiveFile()
    {
        SupervisorHarness harness = new();
        Assert.True((await harness.Supervisor.StartAsync(1, harness.Hash, CancellationToken.None)).Succeeded);
        FakeMihomoChildProcess child = harness.Launcher.Snapshot().Single();

        await harness.Supervisor.DisposeAsync();
        await harness.Supervisor.DisposeAsync();

        Assert.True(child.IsDisposed);
        Assert.Equal(1, child.StopCalls);
        Assert.Empty(harness.EffectiveFiles());
        await harness.DisposeAsync();
    }

    private static void AssertInvariants(
        SupervisorHarness harness,
        Phase phase,
        bool exhausted,
        string label,
        MihomoChildOperationResult? result)
    {
        MihomoServiceIpcSnapshot snapshot = harness.Supervisor.GetSnapshot();
        Assert.True(snapshot.Validate() is null, $"{label}: snapshot invalid ({snapshot.Validate()}).");
        if (result is not null)
        {
            Assert.True(
                result.Succeeded || result.ErrorCode?.StartsWith("service.child.", StringComparison.Ordinal) == true,
                $"{label}: unexpected error code {result.ErrorCode}.");
            Assert.Null(result.Snapshot.Validate());
        }

        FakeMihomoChildProcess[] processes = harness.Launcher.Snapshot();
        int alive = processes.Count(process => !process.HasExited);
        switch (phase)
        {
            case Phase.Stopped:
                Assert.Equal(MihomoServiceChildState.Stopped, snapshot.ChildState);
                Assert.Equal(0, alive);
                Assert.Empty(harness.EffectiveFiles());
                break;
            case Phase.Running:
                Assert.Equal(MihomoServiceChildState.Running, snapshot.ChildState);
                Assert.Equal(1, alive);
                Assert.Equal(processes.Single(process => !process.HasExited).Id, snapshot.ChildProcessId);
                Assert.Single(harness.EffectiveFiles());
                Assert.Null(snapshot.FaultCode);
                break;
            case Phase.FaultedNoChild:
                Assert.Equal(MihomoServiceChildState.Faulted, snapshot.ChildState);
                Assert.Equal(0, alive);
                Assert.Null(snapshot.ChildProcessId);
                Assert.Equal(exhausted ? 1 : 0, harness.EffectiveFiles().Length);
                break;
            case Phase.FaultedOwned:
                Assert.Equal(MihomoServiceChildState.Faulted, snapshot.ChildState);
                Assert.Equal(1, alive);
                Assert.Equal("service.child.stop_failed", snapshot.FaultCode);
                Assert.Equal(processes.Single(process => !process.HasExited).Id, snapshot.ChildProcessId);
                Assert.Single(harness.EffectiveFiles());
                break;
        }

        Assert.False(harness.Launcher.OverlapDetected, $"{label}: two children were alive at once.");
        Assert.True(harness.StagedGenerationFiles().Length <= 9, $"{label}: staged generations are unbounded.");
        foreach (FakeMihomoChildProcess process in processes.Where(process => process.HasExited))
        {
            Assert.True(process.IsDisposed || phase == Phase.FaultedOwned, $"{label}: exited child {process.Name} was never released.");
        }
    }
}
