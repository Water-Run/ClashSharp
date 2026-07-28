using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace ClashSharp.Tests.Architecture;

/// <summary>Specifies primary-instance, host-construction, startup-order, and outer-lifetime contracts.</summary>
public sealed class ApplicationStartupContractTests
{
    /// <summary>Verifies a redirected secondary launch cannot construct or start the application host.</summary>
    [Fact]
    public async Task LaunchAsync_SecondaryInstance_DoesNotConstructHost()
    {
        List<string> trace = [];
        FakePrimaryInstanceBootstrap primaryInstance = new(PrimaryInstanceOwnership.Redirected, trace);
        ProcessLifetimeRunner lifetime = new();
        int hostFactoryCalls = 0;
        int primaryReadyCalls = 0;
        ApplicationBootstrapper bootstrapper = new(
            primaryInstance,
            () =>
            {
                hostFactoryCalls++;
                trace.Add("host-build");
                return new FakeApplicationHost(trace);
            },
            lifetime,
            (_, _) =>
            {
                primaryReadyCalls++;
                return Task.CompletedTask;
            });

        ApplicationLaunchResult result = await bootstrapper.LaunchAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(ApplicationLaunchDisposition.Redirected, result.Disposition);
        Assert.Equal(0, hostFactoryCalls);
        Assert.Equal(0, primaryReadyCalls);
        Assert.Equal(["arbitrate"], trace);
        Assert.False(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies primary arbitration completes before host construction and startup.</summary>
    [Fact]
    public async Task LaunchAsync_PrimaryInstance_BuildsAndStartsHostAfterArbitration()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace);
        ProcessLifetimeRunner lifetime = new();
        ApplicationBootstrapper bootstrapper = new(
            new FakePrimaryInstanceBootstrap(PrimaryInstanceOwnership.Primary, trace),
            () =>
            {
                trace.Add("host-build");
                return host;
            },
            lifetime,
            (_, _) =>
            {
                trace.Add("primary-ready");
                return Task.CompletedTask;
            });

        ApplicationLaunchResult result = await bootstrapper.LaunchAsync(new AppLaunchRequest("--test"), CancellationToken.None);

        Assert.Equal(ApplicationLaunchDisposition.Running, result.Disposition);
        Assert.Equal(["arbitrate", "primary-ready", "host-build", "host-start"], trace);
        Assert.True(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies host construction waits until the early shell has yielded a visible-ready signal.</summary>
    [Fact]
    public async Task LaunchAsync_EarlyShellNotReady_DoesNotConstructHost()
    {
        List<string> trace = [];
        TaskCompletionSource<object?> shellReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int hostFactoryCalls = 0;
        ApplicationBootstrapper bootstrapper = new(
            new FakePrimaryInstanceBootstrap(PrimaryInstanceOwnership.Primary, trace),
            () =>
            {
                hostFactoryCalls++;
                return new FakeApplicationHost(trace);
            },
            new ProcessLifetimeRunner(),
            async (_, cancellationToken) =>
            {
                trace.Add("shell-created");
                await shellReady.Task.WaitAsync(cancellationToken);
            });

        Task<ApplicationLaunchResult> launchTask = bootstrapper.LaunchAsync(
            new AppLaunchRequest(""),
            CancellationToken.None);

        Assert.Equal(["arbitrate", "shell-created"], trace);
        Assert.Equal(0, hostFactoryCalls);

        shellReady.SetResult(null);
        ApplicationLaunchResult result = await launchTask;

        Assert.Equal(ApplicationLaunchDisposition.Running, result.Disposition);
        Assert.Equal(1, hostFactoryCalls);
    }

    /// <summary>Verifies fatal startup remains attached so the visible shell can diagnose and shut down cleanly.</summary>
    [Fact]
    public async Task LaunchAsync_FatalStartup_KeepsHostAttachedForDiagnosticShell()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace)
        {
            StartResult = StartupStepResult.Fatal("startup-test-fatal"),
        };
        ProcessLifetimeRunner lifetime = new();
        ApplicationBootstrapper bootstrapper = new(
            new FakePrimaryInstanceBootstrap(PrimaryInstanceOwnership.Primary, trace),
            () => host,
            lifetime,
            (_, _) =>
            {
                trace.Add("primary-ready");
                return Task.CompletedTask;
            });

        ApplicationLaunchResult result = await bootstrapper.LaunchAsync(
            new AppLaunchRequest(""),
            CancellationToken.None);

        Assert.Equal(ApplicationLaunchDisposition.Fatal, result.Disposition);
        Assert.Equal(["arbitrate", "primary-ready", "host-start"], trace);
        Assert.True(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies a host that throws during startup is stopped and disposed without attachment.</summary>
    [Fact]
    public async Task LaunchAsync_StartupFails_StopsAndDisposesUnattachedHost()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace) { StartException = new InvalidOperationException("startup failed") };
        ProcessLifetimeRunner lifetime = new();
        ApplicationBootstrapper bootstrapper = new(
            new FakePrimaryInstanceBootstrap(PrimaryInstanceOwnership.Primary, trace),
            () =>
            {
                trace.Add("host-build");
                return host;
            },
            lifetime);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => bootstrapper.LaunchAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Equal(["arbitrate", "host-build", "host-start", "host-stop", "host-dispose"], trace);
        Assert.False(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies building AppHost does not instantiate the registered startup coordinator.</summary>
    [Fact]
    public async Task Build_DoesNotResolveStartupCoordinatorUntilStart()
    {
        int constructorCalls = 0;
        await using AppHost host = AppHost.Build(services =>
            services.AddSingleton<IApplicationStartupCoordinator>(_ => new FakeStartupCoordinator(() => constructorCalls++)));

        Assert.Equal(0, constructorCalls);

        StartupStepResult result = await host.StartAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.Equal(1, constructorCalls);
    }

    /// <summary>Verifies startup steps execute deterministically and stop after an exit request.</summary>
    [Fact]
    public async Task StartAsync_OrdersStepsAndStopsAfterExitRequest()
    {
        List<string> trace = [];
        StartupCoordinator coordinator = new(
        [
            new FakeStartupStep("window", 30, StartupStepResult.Succeeded(), trace),
            new FakeStartupStep("helper", 20, StartupStepResult.ExitRequested(), trace),
            new FakeStartupStep("configure", 10, StartupStepResult.Succeeded(), trace),
            new FakeStartupStep("audit", 20, StartupStepResult.Succeeded(), trace),
        ]);

        StartupStepResult result = await coordinator.StartAsync(new AppLaunchRequest("--helper"), CancellationToken.None);

        Assert.Equal(StartupStepOutcome.ExitRequested, result.Outcome);
        Assert.Equal(["configure", "audit", "helper"], trace);
    }

    /// <summary>Verifies asynchronous startup work resumes through the caller's UI synchronization context.</summary>
    [Fact]
    public async Task StartAsync_AsynchronousStep_PreservesCallerSynchronizationContext()
    {
        RecordingSynchronizationContext context = new();
        SynchronizationContext? previousContext = SynchronizationContext.Current;
        Task<StartupStepResult> startupTask;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            StartupCoordinator coordinator = new(
            [
                new AsynchronousStartupStep(),
                new FakeStartupStep("window", 20, StartupStepResult.Succeeded(), []),
            ]);
            startupTask = coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }

        StartupStepResult result = await startupTask;

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
        Assert.True(context.PostCount > 0);
    }

    /// <summary>Verifies every completed startup step publishes its stable identity, outcome, and elapsed time.</summary>
    [Fact]
    public async Task StartAsync_CompletedStep_RecordsOutcomeAndElapsedTime()
    {
        RecordingStartupDiagnosticSink diagnostics = new();
        ManualTimeProvider time = new();
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "recover-mutations",
                150,
                () =>
                {
                    time.Advance(TimeSpan.FromMilliseconds(275));
                    return StartupStepResult.Warning("recovery-warning");
                }),
        ],
        diagnostics,
        time);

        StartupStepResult result = await coordinator.StartAsync(
            new AppLaunchRequest(""),
            CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Warning, result.Outcome);
        StartupDiagnosticRecord completed = Assert.Single(
            diagnostics.Records,
            record => record.Stage == StartupDiagnosticStage.Completed);
        Assert.Equal("recover-mutations", completed.StepName);
        Assert.Equal(150, completed.StepOrder);
        Assert.Equal(StartupStepOutcome.Warning, completed.Outcome);
        Assert.Equal("recovery-warning", completed.DiagnosticCode);
        Assert.Equal(TimeSpan.FromMilliseconds(275), completed.Elapsed);
    }

    /// <summary>Verifies thrown startup exceptions are diagnosed with their step and elapsed time before propagation.</summary>
    [Fact]
    public async Task StartAsync_StepThrows_RecordsFailureBeforeRethrow()
    {
        RecordingStartupDiagnosticSink diagnostics = new();
        ManualTimeProvider time = new();
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "network-behavior",
                450,
                () =>
                {
                    time.Advance(TimeSpan.FromMilliseconds(80));
                    throw new InvalidOperationException("notification unavailable");
                }),
        ],
        diagnostics,
        time);

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Equal("notification unavailable", exception.Message);
        StartupDiagnosticRecord failure = Assert.Single(
            diagnostics.Records,
            record => record.Stage == StartupDiagnosticStage.Failed);
        Assert.Equal("network-behavior", failure.StepName);
        Assert.Equal(450, failure.StepOrder);
        Assert.Equal(TimeSpan.FromMilliseconds(80), failure.Elapsed);
        Assert.Equal(typeof(InvalidOperationException).FullName, failure.ExceptionType);
        Assert.Null(failure.ExceptionMessage);
    }

    [Fact]
    public async Task StartAsync_DiagnosticClockThrows_DoesNotReplaceSuccessfulStartup()
    {
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "window",
                600,
                StartupStepResult.Succeeded),
        ],
        new RecordingStartupDiagnosticSink(),
        new ThrowingTimeProvider());

        StartupStepResult result = await coordinator.StartAsync(
            new AppLaunchRequest(""),
            CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task StartAsync_ElapsedTimeCalculationThrows_DoesNotReplaceSuccessfulStartup()
    {
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "window",
                600,
                StartupStepResult.Succeeded),
        ],
        new RecordingStartupDiagnosticSink(),
        new ThrowingFrequencyTimeProvider());

        StartupStepResult result = await coordinator.StartAsync(
            new AppLaunchRequest(""),
            CancellationToken.None);

        Assert.Equal(StartupStepOutcome.Succeeded, result.Outcome);
    }

    [Fact]
    public async Task StartAsync_ExceptionMetadataThrows_RethrowsOriginalStartupException()
    {
        ThrowingMessageException expected = new();
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "network-behavior",
                450,
                () => throw expected),
        ],
        new RecordingStartupDiagnosticSink());

        ThrowingMessageException actual = await Assert.ThrowsAsync<ThrowingMessageException>(
            () => coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task StartAsync_ExceptionMessageBlocks_RethrowsWithoutReadingMessage()
    {
        using ManualResetEventSlim messageReadStarted = new();
        using ManualResetEventSlim releaseMessage = new();
        BlockingMessageException expected = new(messageReadStarted, releaseMessage);
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "network-behavior",
                450,
                () => throw expected),
        ],
        new RecordingStartupDiagnosticSink());

        Task<StartupStepResult> startup = Task.Run(
            () => coordinator.StartAsync(
                new AppLaunchRequest(""),
                CancellationToken.None));
        try
        {
            Task completed = await Task.WhenAny(
                startup,
                Task.Delay(TimeSpan.FromSeconds(2)));

            Assert.Same(startup, completed);
            BlockingMessageException actual =
                await Assert.ThrowsAsync<BlockingMessageException>(() => startup);
            Assert.Same(expected, actual);
            Assert.False(messageReadStarted.IsSet);
        }
        finally
        {
            releaseMessage.Set();
        }
    }

    [Fact]
    public async Task StartAsync_DiagnosticSinkCancels_PropagatesCancellation()
    {
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "window",
                600,
                StartupStepResult.Succeeded),
        ],
        new ThrowingStartupDiagnosticSink(new OperationCanceledException("diagnostic canceled")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None));
    }

    [Fact]
    public async Task StartAsync_DiagnosticSinkThrowsProcessFatal_PropagatesException()
    {
#pragma warning disable CA2201 // The test must verify that runtime-reserved failures are not contained.
        OutOfMemoryException expected = new("diagnostic allocation failed");
#pragma warning restore CA2201
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "window",
                600,
                StartupStepResult.Succeeded),
        ],
        new ThrowingStartupDiagnosticSink(expected));

        OutOfMemoryException actual = await Assert.ThrowsAsync<OutOfMemoryException>(
            () => coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Same(expected, actual);
    }

    [Fact]
    public async Task StartAsync_DiagnosticSinkWrapsProcessFatal_PropagatesWrapper()
    {
#pragma warning disable CA2201 // The test must verify that runtime-reserved failures are not contained.
        InvalidOperationException expected = new(
            "diagnostic wrapper",
            new OutOfMemoryException("diagnostic allocation failed"));
#pragma warning restore CA2201
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "window",
                600,
                StartupStepResult.Succeeded),
        ],
        new ThrowingStartupDiagnosticSink(expected));

        InvalidOperationException actual = await Assert.ThrowsAsync<InvalidOperationException>(
            () => coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Same(expected, actual);
    }

    /// <summary>Verifies a failed diagnostic cannot replace the startup step failure it describes.</summary>
    [Fact]
    public async Task StartAsync_StepAndFailureDiagnosticBothFail_PreservesBothExceptionGraphs()
    {
        IOException stepFailure = new("startup step failed");
        InvalidOperationException diagnosticFailure = new(
            "diagnostic wrapper",
            Activator.CreateInstance<OutOfMemoryException>());
        StartupCoordinator coordinator = new(
        [
            new CallbackStartupStep(
                "window",
                600,
                () => throw stepFailure),
        ],
        new FailureRecordThrowingStartupDiagnosticSink(diagnosticFailure));

        AggregateException actual = await Assert.ThrowsAsync<AggregateException>(
            () => coordinator.StartAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Contains(stepFailure, actual.InnerExceptions);
        Assert.Contains(diagnosticFailure, actual.InnerExceptions);
    }

    /// <summary>Verifies redirected activation makes hidden/minimized windows visible before activation.</summary>
    [Fact]
    public void BringToFront_MinimizedWindow_ShowsRestoresThenActivates()
    {
        RecordingPrimaryWindow window = new(isMinimized: true);

        PrimaryWindowActivation.BringToFront(window);

        Assert.Equal(["show", "restore", "activate"], window.Trace);
    }

    /// <summary>Verifies a hidden non-minimized window is shown and activated without an invalid restore.</summary>
    [Fact]
    public void BringToFront_HiddenWindow_ShowsThenActivates()
    {
        RecordingPrimaryWindow window = new(isMinimized: false);

        PrimaryWindowActivation.BringToFront(window);

        Assert.Equal(["show", "activate"], window.Trace);
    }

    /// <summary>Verifies concurrent lifetime stop requests stop and dispose one attached host once.</summary>
    [Fact]
    public async Task StopAsync_ConcurrentCallers_StopThenDisposeHostOnce()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace);
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => lifetime.StopAsync(CancellationToken.None)));

        Assert.Equal(["host-stop", "host-dispose"], trace);
        Assert.False(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies an unprepared host remains owned and is never disposed underneath active services.</summary>
    [Fact]
    public async Task StopAsync_HostStopFails_DoesNotDisposeOrReleaseHostOwnership()
    {
        List<string> trace = [];
        FakeApplicationHost host = new(trace)
        {
            StopException = new InvalidOperationException("not prepared"),
        };
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => lifetime.StopAsync(CancellationToken.None));

        Assert.Equal(["host-stop"], trace);
        Assert.True(lifetime.HasAttachedHost);
    }

    /// <summary>Verifies a failed host shutdown coordinator is retried instead of cached forever.</summary>
    [Fact]
    public async Task AppHost_StopAsync_FirstCoordinatorAttemptFails_AllowsLaterRetry()
    {
        FailOnceShutdownCoordinator shutdown = new();
        await using AppHost host = AppHost.Build(services =>
            services.AddSingleton<IApplicationShutdownCoordinator>(shutdown));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StopAsync(CancellationToken.None));
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(2, shutdown.CallCount);
    }

    [Fact]
    public async Task StopAsync_RealAppHostDisposeFailsOnce_RetainsTerminalFailureWithoutReenteringHost()
    {
        await AssertTerminalRealAppHostDisposalFailureAsync(failOnce: true);
    }

    [Fact]
    public async Task StopAsync_RealAppHostDisposeAlwaysFails_RetainsTerminalFailureWithoutReenteringHost()
    {
        await AssertTerminalRealAppHostDisposalFailureAsync(failOnce: false);
    }

    private static async Task AssertTerminalRealAppHostDisposalFailureAsync(bool failOnce)
    {
        DisposeFaultingShutdownCoordinator shutdown = new(failOnce);
        AppHost host = AppHost.Build(services =>
            services.AddSingleton<IApplicationShutdownCoordinator>(_ => shutdown));
        ProcessLifetimeRunner lifetime = new();
        lifetime.AttachHost(host);

        Task firstAttempt = lifetime.StopAsync(CancellationToken.None);
        ApplicationHostDisposalException firstException =
            await Assert.ThrowsAsync<ApplicationHostDisposalException>(() => firstAttempt);

        Assert.False(lifetime.HasAttachedHost);
        Assert.IsType<InvalidOperationException>(firstException.InnerException);
        Task repeatedAttempt = lifetime.StopAsync(CancellationToken.None);
        Assert.Same(firstAttempt, repeatedAttempt);
        ApplicationHostDisposalException repeatedException =
            await Assert.ThrowsAsync<ApplicationHostDisposalException>(() => repeatedAttempt);
        Assert.Same(firstException, repeatedException);
        Assert.Equal(1, shutdown.StopCallCount);
        Assert.Equal(1, shutdown.DisposeCallCount);
    }

    private sealed class FakePrimaryInstanceBootstrap(
        PrimaryInstanceOwnership ownership,
        ICollection<string> trace) : IPrimaryInstanceBootstrap
    {
        public Task<PrimaryInstanceOwnership> AcquireAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trace.Add("arbitrate");
            return Task.FromResult(ownership);
        }
    }

    private sealed class FakeApplicationHost(ICollection<string> trace) : IApplicationHost
    {
        public Exception? StartException { get; init; }

        public Exception? StopException { get; init; }

        public StartupStepResult StartResult { get; init; } = StartupStepResult.Succeeded();

        public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            trace.Add("host-start");
            return StartException is null
                ? Task.FromResult(StartResult)
                : Task.FromException<StartupStepResult>(StartException);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            trace.Add("host-stop");
            return StopException is null
                ? Task.CompletedTask
                : Task.FromException(StopException);
        }

        public ValueTask DisposeAsync()
        {
            trace.Add("host-dispose");
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStartupCoordinator(Action onConstructed) : IApplicationStartupCoordinator
    {
        private readonly object _constructionProbe = Invoke(onConstructed);

        public Task<StartupStepResult> StartAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            GC.KeepAlive(_constructionProbe);
            return Task.FromResult(StartupStepResult.Succeeded());
        }

        private static object Invoke(Action action)
        {
            action();
            return new object();
        }
    }

    private sealed class FailOnceShutdownCoordinator : IApplicationShutdownCoordinator
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _callCount) == 1)
            {
                return Task.FromException(new InvalidOperationException("runtime not prepared"));
            }

            return Task.CompletedTask;
        }
    }

    private sealed class DisposeFaultingShutdownCoordinator(bool failOnce) :
        IApplicationShutdownCoordinator,
        IAsyncDisposable
    {
        private int _disposeCallCount;
        private int _stopCallCount;

        public int DisposeCallCount => Volatile.Read(ref _disposeCallCount);

        public int StopCallCount => Volatile.Read(ref _stopCallCount);

        public Task StopAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _stopCallCount);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            int callCount = Interlocked.Increment(ref _disposeCallCount);
            if (!failOnce || callCount == 1)
            {
                return ValueTask.FromException(
                    new InvalidOperationException("Injected service disposal failure."));
            }

            return ValueTask.CompletedTask;
        }
    }

    private sealed class FakeStartupStep(
        string name,
        int order,
        StartupStepResult result,
        ICollection<string> trace) : IStartupStep
    {
        public string Name => name;

        public int Order => order;

        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            trace.Add(Name);
            return Task.FromResult(result);
        }
    }

    private sealed class AsynchronousStartupStep : IStartupStep
    {
        public string Name => "asynchronous";

        public int Order => 10;

        public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
            return StartupStepResult.Succeeded();
        }
    }

    private sealed class CallbackStartupStep(
        string name,
        int order,
        Func<StartupStepResult> callback) : IStartupStep
    {
        public string Name => name;

        public int Order => order;

        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(callback());
        }
    }

    private sealed class RecordingStartupDiagnosticSink : IStartupDiagnosticSink
    {
        public List<StartupDiagnosticRecord> Records { get; } = [];

        public void Record(StartupDiagnosticRecord record)
        {
            Records.Add(record);
        }
    }

    private sealed class ThrowingStartupDiagnosticSink(Exception exception) : IStartupDiagnosticSink
    {
        public void Record(StartupDiagnosticRecord record)
        {
            throw exception;
        }
    }

    private sealed class FailureRecordThrowingStartupDiagnosticSink(Exception failure) :
        IStartupDiagnosticSink
    {
        public void Record(StartupDiagnosticRecord record)
        {
        }

        public void RecordFailure(StartupDiagnosticRecord record, Exception exception)
        {
            throw failure;
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _timestamp;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => _timestamp;

        public void Advance(TimeSpan elapsed)
        {
            _timestamp += elapsed.Ticks;
        }
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override long GetTimestamp()
        {
            throw new InvalidOperationException("diagnostic clock unavailable");
        }
    }

    private sealed class ThrowingFrequencyTimeProvider : TimeProvider
    {
        public override long TimestampFrequency =>
            throw new InvalidOperationException("diagnostic frequency unavailable");

        public override long GetTimestamp() => 1;
    }

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message =>
            throw new InvalidOperationException("exception metadata unavailable");
    }

    private sealed class BlockingMessageException(
        ManualResetEventSlim messageReadStarted,
        ManualResetEventSlim releaseMessage) : Exception
    {
        public override string Message
        {
            get
            {
                messageReadStarted.Set();
                releaseMessage.Wait();
                return "message released";
            }
        }
    }

    private sealed class RecordingPrimaryWindow(bool isMinimized) : IPrimaryWindowActivationTarget
    {
        public bool IsMinimized { get; } = isMinimized;

        public List<string> Trace { get; } = [];

        public void Show() => Trace.Add("show");

        public void Restore() => Trace.Add("restore");

        public void Activate() => Trace.Add("activate");
    }

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }
    }
}
