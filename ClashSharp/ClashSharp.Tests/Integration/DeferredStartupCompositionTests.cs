using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.Extensions.DependencyInjection;

namespace ClashSharp.Tests.Integration;

/// <summary>Exercises actual host composition so recovery controls when data-dependent services can be constructed.</summary>
public sealed class DeferredStartupCompositionTests
{
    [Fact]
    public async Task Start_RecoveryCompletesBeforeConstructingADataDependentService()
    {
        State state = new();
        await using AppHost host = CreateHost(state);
        Assert.Empty(state.Trace);

        Task<StartupStepResult> startup = host.StartAsync(new AppLaunchRequest(""), CancellationToken.None);
        Assert.Equal(["create-recovery", "run-recovery"], state.Trace);
        state.Recovery.TrySetResult(StartupStepResult.Succeeded());

        Assert.Equal(StartupStepOutcome.Succeeded, (await startup).Outcome);
        Assert.Equal(["create-recovery", "run-recovery", "recovery-complete", "create-data", "create-consumer", "run-consumer"], state.Trace);
        Assert.Equal("recovered-generation", state.ObservedDataRoot);
    }

    [Theory]
    [InlineData(StartupStepOutcome.Fatal)]
    [InlineData(StartupStepOutcome.ExitRequested)]
    public async Task Start_EarlyTerminalResultNeverConstructsLaterStepsOrTheirDependencies(StartupStepOutcome outcome)
    {
        State state = new();
        AppHost host = CreateHost(state);
        state.Recovery.SetResult(outcome == StartupStepOutcome.Fatal
            ? StartupStepResult.Fatal("recovery-required") : StartupStepResult.ExitRequested());

        StartupStepResult result = await host.StartAsync(new AppLaunchRequest(""), CancellationToken.None);
        await host.DisposeAsync();

        Assert.Equal(outcome, result.Outcome);
        Assert.Equal(["create-recovery", "run-recovery", "dispose-recovery"], state.Trace);
        Assert.Null(state.ObservedDataRoot);
    }

    [Fact]
    public async Task Start_PreCancelledRequestDoesNotConstructAnyStep()
    {
        State state = new();
        AppHost host = CreateHost(state);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(new AppLaunchRequest(""), new CancellationToken(true)));
        await host.DisposeAsync();

        Assert.Empty(state.Trace);
    }

    [Fact]
    public async Task Start_CancelledRecoveryDoesNotConstructTheNextDataReader()
    {
        State state = new();
        AppHost host = CreateHost(state);
        using CancellationTokenSource cancellation = new();

        Task<StartupStepResult> startup = host.StartAsync(new AppLaunchRequest(""), cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => startup);
        await host.DisposeAsync();

        Assert.Equal(["create-recovery", "run-recovery", "dispose-recovery"], state.Trace);
        Assert.Null(state.ObservedDataRoot);
    }

    [Fact]
    public async Task Dispose_ReleasesOnlyConstructedServicesExactlyOnceInDependencyOrder()
    {
        State state = new();
        AppHost host = CreateHost(state);
        state.Recovery.SetResult(StartupStepResult.Succeeded());
        await host.StartAsync(new AppLaunchRequest(""), CancellationToken.None);
        state.Trace.Clear();

        await host.DisposeAsync();
        await host.DisposeAsync();

        Assert.Equal(["dispose-consumer", "dispose-data", "dispose-recovery"], state.Trace);
    }

    [Fact]
    public async Task Start_ConstructorFailureIsAttributedToTheCorrectStepAndStopsLaterResolution()
    {
        State state = new();
        IOException failure = new("Data root unavailable after recovery");
        state.DataFailure = failure;
        state.Recovery.SetResult(StartupStepResult.Succeeded());
        Diagnostics diagnostics = new();
        await using AppHost host = CreateHost(state, services =>
        {
            services.AddSingleton<IStartupDiagnosticSink>(diagnostics);
            services.AddSingleton<LaterStep>(_ => throw new InvalidOperationException("Must remain unresolved"));
            services.AddDeferredStartupStep<LaterStep>("later", 30);
        });

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => host.StartAsync(new AppLaunchRequest(""), CancellationToken.None)));

        Assert.Equal(["create-recovery", "run-recovery", "recovery-complete", "create-data"], state.Trace);
        StartupDiagnosticRecord failed = Assert.Single(diagnostics.Records, record => record.Stage == StartupDiagnosticStage.Failed);
        Assert.Equal("consumer", failed.StepName);
        Assert.Equal(20, failed.StepOrder);
        Assert.Same(failure, Assert.Single(diagnostics.Failures));
    }

    [Theory]
    [InlineData("wrong-name", 10)]
    [InlineData("configured", 11)]
    public async Task Start_MetadataMismatchIsRejectedBeforeExecutingTheResolvedStep(string actualName, int actualOrder)
    {
        int executions = 0;
        await using AppHost host = AppHost.Build(services =>
        {
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddSingleton<ConfiguredStep>(_ => new(actualName, actualOrder, () => executions++));
            services.AddDeferredStartupStep<ConfiguredStep>("configured", 10);
        });

        await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task Start_ExistingStepFactoryIsPreservedAndResolvedOnce()
    {
        int constructions = 0;
        int executions = 0;
        await using AppHost host = AppHost.Build(services =>
        {
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddSingleton<ConfiguredStep>(_ =>
            {
                constructions++;
                return new("configured", 10, () => executions++);
            });
            services.AddDeferredStartupStep<ConfiguredStep>("configured", 10);
        });
        Assert.Equal(0, constructions);

        await host.StartAsync(new AppLaunchRequest(""), CancellationToken.None);

        Assert.Equal(1, constructions);
        Assert.Equal(1, executions);
    }

    [Fact]
    public async Task Start_CancellationDuringConstructionPreventsTheStepsEffects()
    {
        int executions = 0;
        using CancellationTokenSource cancellation = new();
        await using AppHost host = AppHost.Build(services =>
        {
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddSingleton<ConfiguredStep>(_ =>
            {
                cancellation.Cancel();
                return new("configured", 10, () => executions++);
            });
            services.AddDeferredStartupStep<ConfiguredStep>("configured", 10);
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(new AppLaunchRequest(""), cancellation.Token));

        Assert.Equal(0, executions);
    }

    [Fact]
    public async Task Start_DuplicateMetadataIsRejectedWithoutConstructingAnyStep()
    {
        State state = new();
        await using AppHost host = CreateHost(state, services => services.AddDeferredStartupStep<RecoveryStep>("recovery", 10));

        await Assert.ThrowsAsync<ArgumentException>(() => host.StartAsync(new AppLaunchRequest(""), CancellationToken.None));

        Assert.Empty(state.Trace);
    }

    [Fact]
    public void Build_UnresolvableConcreteDependenciesRemainSubjectToHostValidation()
    {
        Assert.Throws<AggregateException>(() => AppHost.Build(services =>
        {
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddDeferredStartupStep<MissingDependencyStep>("invalid", 10);
        }));
    }

    private static AppHost CreateHost(State state, Action<IServiceCollection>? configure = null) => AppHost.Build(services =>
    {
        services.AddSingleton(state);
        services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
        services.AddSingleton<DataReader>();
        services.AddDeferredStartupStep<ConsumerStep>("consumer", 20);
        services.AddDeferredStartupStep<RecoveryStep>("recovery", 10);
        configure?.Invoke(services);
    });

    private sealed class State
    {
        public List<string> Trace { get; } = [];
        public TaskCompletionSource<StartupStepResult> Recovery { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string DataRoot { get; set; } = "legacy-generation";
        public string? ObservedDataRoot { get; set; }
        public Exception? DataFailure { get; set; }
    }

    private sealed class RecoveryStep : IStartupStep, IAsyncDisposable
    {
        private readonly State _state;
        public RecoveryStep(State state) { _state = state; state.Trace.Add("create-recovery"); }
        public string Name => "recovery";
        public int Order => 10;
        public async Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            _state.Trace.Add("run-recovery");
            StartupStepResult result = await _state.Recovery.Task.WaitAsync(cancellationToken);
            if (result.Outcome == StartupStepOutcome.Succeeded)
            {
                _state.DataRoot = "recovered-generation";
                _state.Trace.Add("recovery-complete");
            }
            return result;
        }
        public ValueTask DisposeAsync() { _state.Trace.Add("dispose-recovery"); return ValueTask.CompletedTask; }
    }

    private sealed class DataReader : IAsyncDisposable
    {
        private readonly State _state;
        public DataReader(State state)
        {
            _state = state;
            state.Trace.Add("create-data");
            if (state.DataFailure is not null) { throw state.DataFailure; }
            state.ObservedDataRoot = state.DataRoot;
        }
        public ValueTask DisposeAsync() { _state.Trace.Add("dispose-data"); return ValueTask.CompletedTask; }
    }

    private sealed class ConsumerStep : IStartupStep, IAsyncDisposable
    {
        private readonly State _state;
        public ConsumerStep(State state, DataReader data)
        {
            ArgumentNullException.ThrowIfNull(data);
            _state = state;
            state.Trace.Add("create-consumer");
        }
        public string Name => "consumer";
        public int Order => 20;
        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            _state.Trace.Add("run-consumer");
            return Task.FromResult(StartupStepResult.Succeeded());
        }
        public ValueTask DisposeAsync() { _state.Trace.Add("dispose-consumer"); return ValueTask.CompletedTask; }
    }

    private sealed class ConfiguredStep(string name, int order, Action execute) : IStartupStep
    {
        public string Name => name;
        public int Order => order;
        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            execute();
            return Task.FromResult(StartupStepResult.Succeeded());
        }
    }

    private sealed class LaterStep : IStartupStep
    {
        public string Name => "later";
        public int Order => 30;
        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken) => Task.FromResult(StartupStepResult.Succeeded());
    }

    private interface IMissingDependency;

    private sealed class MissingDependencyStep(IMissingDependency missing) : IStartupStep
    {
        public string Name => "invalid";
        public int Order => 10;
        public Task<StartupStepResult> ExecuteAsync(AppLaunchRequest request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(missing);
            return Task.FromResult(StartupStepResult.Succeeded());
        }
    }

    private sealed class Diagnostics : IStartupDiagnosticSink
    {
        public List<StartupDiagnosticRecord> Records { get; } = [];
        public List<Exception> Failures { get; } = [];
        public void Record(StartupDiagnosticRecord record) => Records.Add(record);
        public void RecordFailure(StartupDiagnosticRecord record, Exception exception) { Records.Add(record); Failures.Add(exception); }
    }
}
