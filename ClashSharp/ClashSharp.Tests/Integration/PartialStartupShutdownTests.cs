extern alias ClashSharpUi;

using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Model;
using Microsoft.Extensions.DependencyInjection;
using ClashSharpAppHostFactory = ClashSharpUi::ClashSharp.Hosting.ClashSharpAppHostFactory;
using InstallerTransactionState = ClashSharpUi::ClashSharp.Service.InstallerTransactionState;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises startup failure through actual host shutdown and service disposal.</summary>
public sealed class PartialStartupShutdownTests
{
    [Fact]
    public async Task ProductionComposition_StopBeforeStartDoesNotRequireSettingsCredentialsOrDesktopServices()
    {
        Diagnostics diagnostics = new();
        // Build and Stop only: never run product startup, open a window, or request native effects.
        await using AppHost host = ClashSharpAppHostFactory.Build(new(""),
            _ => throw new InvalidOperationException("A window must not be created by shutdown."),
            new ApplicationLifetimeRequestChannel(), diagnostics, InstallerTransactionState.Clear);

        await host.StopAsync(CancellationToken.None);

        Assert.Empty(diagnostics.Records);
    }

    [Theory]
    [InlineData(StartupStepOutcome.Fatal)]
    [InlineData(StartupStepOutcome.ExitRequested)]
    public async Task EarlyTerminalStartup_StopDoesNotConstructLaterRepositoriesOrReadNetworkSettings(StartupStepOutcome outcome)
    {
        State state = new();
        state.FirstResult = outcome == StartupStepOutcome.Fatal ? StartupStepResult.Fatal("data-unavailable") : StartupStepResult.ExitRequested();
        AppHost host = CreateHost(state);

        Assert.Equal(outcome, (await host.StartAsync(new(""), CancellationToken.None)).Outcome);
        await host.StopAsync(CancellationToken.None);
        await host.DisposeAsync();

        Assert.Equal(["first"], state.Trace);
        Assert.Equal(MutationAdmissionState.ClosedForShutdown, state.Admission.State);
    }

    [Fact]
    public async Task FaultedStartup_StopStillCleansOnlyCreatedServicesAndPreservesOriginalFailure()
    {
        State state = new() { FirstFailure = new IOException("generation unavailable") };
        await using AppHost host = CreateHost(state);
        Task<StartupStepResult> startup = host.StartAsync(new(""), CancellationToken.None);

        Assert.Same(state.FirstFailure, await Assert.ThrowsAsync<IOException>(() => startup));
        await host.StopAsync(CancellationToken.None);

        Assert.True(startup.IsFaulted);
        Assert.Equal(["first"], state.Trace);
    }

    [Fact]
    public async Task CancelledStartup_StopDoesNotCreateUnreachedRuntime()
    {
        State state = new() { FirstFailure = new OperationCanceledException(new CancellationToken(true)) };
        await using AppHost host = CreateHost(state);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(new(""), CancellationToken.None));
        await host.StopAsync(CancellationToken.None);

        Assert.Equal(["first"], state.Trace);
    }

    [Fact]
    public async Task StopBeforeStart_DoesNotResolveStartupGraphAndRejectsSubsequentStart()
    {
        State state = new();
        await using AppHost host = CreateHost(state);

        await host.StopAsync(CancellationToken.None);

        Assert.Empty(state.Trace);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(new(""), CancellationToken.None));
    }

    [Fact]
    public async Task StopDuringStartup_WaitsForAcceptedWorkBeforeCapturingCleanupOwnership()
    {
        State state = new() { ContinueFirst = Signal() };
        await using AppHost host = CreateHost(state);
        Task<StartupStepResult> startup = host.StartAsync(new(""), CancellationToken.None);
        Task stopping = host.StopAsync(CancellationToken.None);
        await Task.Yield();
        Assert.False(stopping.IsCompleted);
        Assert.Equal(MutationAdmissionState.Open, state.Admission.State);

        state.ContinueFirst.SetResult();
        await startup;
        await stopping;

        Assert.Equal(["first", "bind-network", "create-producer", "start-producer", "quiesce-producer", "intent", "network", "stop-producer"], state.Trace);
    }

    [Fact]
    public async Task FailureAfterNetworkOwnership_PreservesNetworkCleanupWithoutConstructingLaterProducers()
    {
        State state = new() { StopAfterNetwork = true };
        await using AppHost host = CreateHost(state);
        Assert.Equal(StartupStepOutcome.Fatal, (await host.StartAsync(new(""), CancellationToken.None)).Outcome);

        await host.StopAsync(CancellationToken.None);

        Assert.Equal(["first", "bind-network", "intent", "network"], state.Trace);
    }

    [Fact]
    public async Task ProducerStartFailure_IsStillQuiescedStoppedAndDisposedOnce()
    {
        State state = new() { FailProducerStart = true };
        AppHost host = CreateHost(state);
        await Assert.ThrowsAsync<IOException>(() => host.StartAsync(new(""), CancellationToken.None));

        await host.StopAsync(CancellationToken.None);
        await host.StopAsync(CancellationToken.None);
        await host.DisposeAsync();
        await host.DisposeAsync();

        Assert.Equal(["first", "bind-network", "create-producer", "start-producer", "quiesce-producer", "intent", "network", "stop-producer", "dispose-producer"], state.Trace);
    }

    [Fact]
    public async Task CoordinatorCreatedEarly_IncludesServicesConstructedLaterInDeclaredQuiescenceOrder()
    {
        State state = new();
        RuntimeLifecycleCoordinator lifecycle = new(state.Admission, state.Runtime);
        Producer later = new("later", state);
        Producer earlier = new("earlier", state);
        state.Runtime.RegisterParticipant(later, 200);
        state.Runtime.RegisterParticipant(earlier, 100);
        state.Runtime.RegisterParticipant(earlier, 100);
        state.BindNetwork();
        state.Trace.Clear();

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, (await lifecycle.ShutdownAsync(CancellationToken.None)).Outcome);

        Assert.Equal(["quiesce-earlier", "quiesce-later", "intent", "network", "stop-later", "stop-earlier"], state.Trace);
        // The registry records services but their DI container still owns disposal.
        Assert.DoesNotContain(state.Trace, entry => entry.StartsWith("dispose-", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NetworkAbort_ResumesCreatedServicesAndRetryCapturesNewlyConstructedServices()
    {
        State state = new() { NetworkOutcome = MutationOutcome.Compensated };
        RuntimeLifecycleCoordinator lifecycle = new(state.Admission, state.Runtime);
        state.BindNetwork();
        state.Runtime.RegisterParticipant(new Producer("first", state), 100);
        state.Trace.Clear();

        Assert.Equal(RuntimeShutdownOutcome.Aborted, (await lifecycle.ShutdownAsync(CancellationToken.None)).Outcome);
        Assert.Equal(["quiesce-first", "intent", "network", "resume-first"], state.Trace);
        Assert.Equal(MutationAdmissionState.Open, state.Admission.State);

        state.Runtime.RegisterParticipant(new Producer("later", state), 200);
        state.NetworkOutcome = MutationOutcome.Succeeded;
        state.Trace.Clear();
        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, (await lifecycle.ShutdownAsync(CancellationToken.None)).Outcome);
        Assert.Equal(["quiesce-first", "quiesce-later", "intent", "network", "stop-later", "stop-first"], state.Trace);
    }

    [Fact]
    public async Task ShutdownDrain_RejectsLateProducersAndNetworkOwnershipBeforeAndAfterCommit()
    {
        State state = new();
        RuntimeLifecycleCoordinator lifecycle = new(state.Admission, state.Runtime);
        MutationAdmissionLease ordinary = state.Admission.AcquireOrdinary();
        TaskCompletionSource closing = Signal();
        using CancellationTokenRegistration registration = ordinary.RevocationToken.Register(() => closing.TrySetResult());
        Task<RuntimeShutdownResult> stopping = lifecycle.ShutdownAsync(CancellationToken.None);
        try
        {
            await closing.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Throws<InvalidOperationException>(() => state.Runtime.RegisterParticipant(new Producer("late", state)));
            Assert.Throws<InvalidOperationException>(state.BindNetwork);
        }
        finally
        {
            ordinary.Dispose();
            await stopping;
        }

        Assert.Throws<InvalidOperationException>(() => state.Runtime.RegisterParticipant(new Producer("after-stop", state)));
        Assert.Throws<InvalidOperationException>(state.BindNetwork);
    }

    [Fact]
    public async Task DataRemovalBeforeNetworkInitialization_DoesNotClaimNetworkWasDisabled()
    {
        State state = new();
        RuntimeLifecycleCoordinator lifecycle = new(state.Admission, state.Runtime);
        state.Runtime.RegisterParticipant(new Producer("active", state));
        state.Trace.Clear();

        RuntimeShutdownResult result = await lifecycle.PrepareDataRemovalAsync(CancellationToken.None);

        Assert.Equal(RuntimeShutdownOutcome.Aborted, result.Outcome);
        Assert.Equal("shutdown-network-uninitialized", result.ErrorCode);
        Assert.Equal(MutationAdmissionState.Open, state.Admission.State);
        Assert.Equal(["quiesce-active", "resume-active"], state.Trace);
    }

    [Fact]
    public async Task RecoveryHelper_ShutsDownCreatedServicesWithoutReadingNormalExitIntent()
    {
        State state = new();
        RuntimeLifecycleCoordinator lifecycle = new(state.Admission, state.Runtime, networkPolicy: RuntimeShutdownNetworkPolicy.PreserveCurrentState);
        state.Runtime.RegisterNetwork(new Network(state), () => throw new InvalidOperationException("Helper cannot read normal exit intent."));
        state.Runtime.RegisterParticipant(new Producer("active", state));
        state.Trace.Clear();

        Assert.Equal(RuntimeShutdownOutcome.PreparedForHostDisposal, (await lifecycle.ShutdownAsync(CancellationToken.None)).Outcome);

        Assert.Equal(["quiesce-active", "stop-active"], state.Trace);
    }

    [Fact]
    public async Task StopDuringFaultingStartup_AwaitsFailureAndDoesNotConstructLaterServices()
    {
        State state = new() { ContinueFirst = Signal(), FirstFailure = new IOException("recovery interrupted") };
        await using AppHost host = CreateHost(state);
        Task<StartupStepResult> startup = host.StartAsync(new(""), CancellationToken.None);
        Task stopping = host.StopAsync(CancellationToken.None);
        await Task.Yield();
        Assert.False(stopping.IsCompleted);

        state.ContinueFirst.SetResult();
        await Assert.ThrowsAsync<IOException>(() => startup);
        await stopping;

        Assert.Equal(["first"], state.Trace);
    }

    [Fact]
    public async Task ProcessFatalStartupFailure_IsNotSwallowedByTheStopAwait()
    {
        AggregateException failure = new(new IOException("outer"), Activator.CreateInstance<OutOfMemoryException>());
        State state = new() { FirstFailure = failure };
        await using AppHost host = CreateHost(state);
        Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => host.StartAsync(new(""), CancellationToken.None)));

        Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => host.StopAsync(CancellationToken.None)));

        Assert.Equal(MutationAdmissionState.Open, state.Admission.State);
        Assert.Equal(["first"], state.Trace);
    }

    [Fact]
    public void Registry_RejectsAmbiguousNamesAndSecondNetworkOwner()
    {
        State state = new();
        state.Runtime.RegisterParticipant(new Producer("same", state));
        Assert.Throws<ArgumentException>(() => state.Runtime.RegisterParticipant(new Producer("same", state)));
        Assert.Throws<ArgumentException>(() => state.Runtime.RegisterParticipant(new Producer(" ", state)));
        state.BindNetwork();
        Assert.Throws<InvalidOperationException>(state.BindNetwork);
    }

    private static AppHost CreateHost(State state) => AppHost.Build(services =>
    {
        services.AddSingleton(state);
        services.AddSingleton(state.Runtime);
        services.AddSingleton(state.Admission);
        services.AddSingleton(provider => new RuntimeLifecycleCoordinator(state.Admission, state.Runtime));
        services.AddSingleton<IApplicationShutdownCoordinator>(provider => provider.GetRequiredService<RuntimeLifecycleCoordinator>());
        services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
        services.AddSingleton<Producer>(_ => state.Runtime.RegisterParticipant(new Producer("producer", state)));
        services.AddDeferredStartupStep<FirstStep>("first", 50);
        services.AddDeferredStartupStep<NetworkStep>("network-ownership", 145);
        services.AddDeferredStartupStep<ProducerStep>("producer", 400);
    });

    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class State
    {
        public List<string> Trace { get; } = [];
        public RuntimeLifetimeRegistry Runtime { get; } = new();
        public MutationAdmissionBarrier Admission { get; } = new();
        public StartupStepResult FirstResult { get; set; } = StartupStepResult.Succeeded();
        public Exception? FirstFailure { get; init; }
        public TaskCompletionSource? ContinueFirst { get; init; }
        public bool StopAfterNetwork { get; init; }
        public bool FailProducerStart { get; init; }
        public MutationOutcome NetworkOutcome { get; set; } = MutationOutcome.Succeeded;
        public void BindNetwork()
        {
            Trace.Add("bind-network");
            Runtime.RegisterNetwork(new Network(this), () =>
            {
                Trace.Add("intent");
                return NetworkIntent.Shutdown(ClashSharpMode.Disabled, false, 7890);
            });
        }
    }

    private sealed class FirstStep(State state) : IStartupStep
    {
        public string Name => "first";
        public int Order => 50;
        public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            state.Trace.Add("first");
            if (state.ContinueFirst is not null) { await state.ContinueFirst.Task.WaitAsync(cancellationToken); }
            if (state.FirstFailure is not null) { throw state.FirstFailure; }
            return state.FirstResult;
        }
    }

    private sealed class NetworkStep(State state) : IStartupStep
    {
        public string Name => "network-ownership";
        public int Order => 145;
        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            state.BindNetwork();
            return Task.FromResult(state.StopAfterNetwork ? StartupStepResult.Fatal("recovery-required") : StartupStepResult.Succeeded());
        }
    }

    private sealed class ProducerStep(Producer producer) : IStartupStep
    {
        public string Name => "producer";
        public int Order => 400;
        public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            await producer.StartAsync(cancellationToken);
            return StartupStepResult.Succeeded();
        }
    }

    private sealed class Producer : IRuntimeParticipant, IAsyncDisposable
    {
        private readonly State _state;
        public Producer(string name, State state) { Name = name; _state = state; state.Trace.Add("create-" + name); }
        public string Name { get; }
        public Task StartAsync(CancellationToken cancellationToken)
        {
            _state.Trace.Add("start-" + Name);
            return _state.FailProducerStart ? Task.FromException(new IOException("start failed after allocating work")) : Task.CompletedTask;
        }
        public Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
        {
            _state.Trace.Add("quiesce-" + Name);
            return Task.FromResult(new QuiescedState(true));
        }
        public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken) { _state.Trace.Add("resume-" + Name); return Task.CompletedTask; }
        public Task StopAsync(CancellationToken cancellationToken) { _state.Trace.Add("stop-" + Name); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { _state.Trace.Add("dispose-" + Name); return ValueTask.CompletedTask; }
    }

    private sealed class Network(State state) : IRuntimeShutdownNetworkCoordinator
    {
        public Task<MutationResult<NetworkTransitionResult>> ApplyShutdownAsync(NetworkIntent intent,
            MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
        {
            state.Admission.EnsureActiveExclusiveLease(admissionLease);
            state.Trace.Add("network");
            return Task.FromResult(new MutationResult<NetworkTransitionResult>(Guid.NewGuid(), state.NetworkOutcome,
                new(ClashSharpMode.Disabled, false, false, false, 7890, "disabled"), null));
        }
    }

    private sealed class Diagnostics : IStartupDiagnosticSink
    {
        public List<StartupDiagnosticRecord> Records { get; } = [];
        public void Record(StartupDiagnosticRecord record) => Records.Add(record);
        public void RecordFailure(StartupDiagnosticRecord record, Exception exception) => Records.Add(record);
    }
}
