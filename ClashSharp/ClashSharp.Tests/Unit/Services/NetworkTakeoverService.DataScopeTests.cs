using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class NetworkTakeoverServiceTests
{
    [Fact]
    public async Task BoundTakeover_UsesOwnedConfigurationSelectionsAndSamplingInsteadOfRootCallbacks()
    {
        await using ScopedLogDatabase logs = new();
        FakeNetworkTakeoverCoreConfiguration rootConfiguration = new();
        FakeNetworkTakeoverCore core = new();
        ScopeSelections rootSelections = new();
        NetworkTakeoverService root = CreateService(configuration: rootConfiguration, core: core,
            selections: rootSelections, flushTraffic: _ => throw new InvalidOperationException("root sampling must not run"));
        ScopeConfiguration store = new();
        ScopeSelections selections = new();
        List<string> samples = [];
        NetworkTakeoverService scoped = root.BindDataScope(store, selections,
            _ => { samples.Add("refresh"); return Task.CompletedTask; },
            _ => { samples.Add("sample"); return Task.CompletedTask; }, logs.Store);

        await scoped.ApplyModeAsync(ClashSharpMode.RuleTakeover, false, 12345, CancellationToken.None);

        Assert.Equal(store.GetState().DataGenerationId, scoped.DataGenerationId);
        Assert.Single(store.Configuration.Requests);
        Assert.Empty(rootConfiguration.Requests);
        Assert.Empty(rootSelections.Restored);
        Assert.Single(selections.Restored);
        Assert.Equal(["refresh", "sample"], samples);
        Assert.Equal(store.GetState().DataGenerationId, Assert.Single(core.RestartedStates).DataGenerationId);
        Assert.Empty(logs.Store.GetRecentLogs(5));
    }

    [Fact]
    public async Task RootAndBoundTakeovers_ShareOneNativeTransitionUntilFinalSamplingCompletes()
    {
        await using ScopedLogDatabase logs = new();
        FakeNetworkTakeoverCoreConfiguration rootConfiguration = new();
        NetworkTakeoverService root = CreateService(configuration: rootConfiguration);
        ScopeConfiguration firstStore = new();
        ScopeConfiguration secondStore = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        ManualDeadlineTimeProvider clock = new();
        NetworkTakeoverService first = root.BindDataScope(firstStore, new ScopeSelections(), _ => Task.CompletedTask,
            async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }, logs.Store, clock);
        NetworkTakeoverService second = root.BindDataScope(secondStore, new ScopeSelections(), _ => Task.CompletedTask, _ => Task.CompletedTask, logs.Store);
        Task firstOperation = first.ApplyModeAsync(ClashSharpMode.Disabled, false, 12345, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task secondOperation = second.ApplyModeAsync(ClashSharpMode.Disabled, false, 12345, CancellationToken.None);
        Task rootOperation = root.ApplyModeAsync(ClashSharpMode.Disabled, false, 12345, CancellationToken.None);
        try
        {
            Assert.False(secondOperation.IsCompleted);
            Assert.False(rootOperation.IsCompleted);
            Assert.Empty(secondStore.Configuration.Requests);
            Assert.Empty(rootConfiguration.Requests);
        }
        finally { release.TrySetResult(); }
        await Task.WhenAll(firstOperation, secondOperation, rootOperation).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(firstStore.Configuration.Requests);
        Assert.Single(secondStore.Configuration.Requests);
        Assert.Single(rootConfiguration.Requests);
        Assert.True(Assert.Single(clock.Timers).IsDisposed);
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    [InlineData("timeout-sampling")]
    [InlineData("cancel-sampling")]
    [InlineData("success")]
    public async Task BoundFinalSample_UsesOwnedDiagnosticsAndPreservesCallerCancellation(string outcome)
    {
        await using ScopedLogDatabase logs = new();
        using CancellationTokenSource cancellation = new();
        FakeNetworkTakeoverCore core = new();
        NetworkTakeoverService root = CreateService(core: core);
        ScopeConfiguration store = new();
        ManualDeadlineTimeProvider clock = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int samplingCalls = 0;
        bool failsDuringSampling = outcome.EndsWith("-sampling", StringComparison.Ordinal);
        bool callerCancels = outcome.StartsWith("cancel", StringComparison.Ordinal);
        bool timesOut = outcome.StartsWith("timeout", StringComparison.Ordinal);

        async Task CompleteStageAsync(CancellationToken token)
        {
            entered.TrySetResult();
            if (callerCancels) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (timesOut) { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            if (outcome == "failure") { throw new IOException("sample unavailable"); }
        }

        NetworkTakeoverService scoped = root.BindDataScope(store, new ScopeSelections(),
            token => failsDuringSampling ? Task.CompletedTask : CompleteStageAsync(token),
            token =>
            {
                ++samplingCalls;
                return failsDuringSampling ? CompleteStageAsync(token) : Task.CompletedTask;
            }, logs.Store, clock);
        Task operation = scoped.ApplyModeAsync(ClashSharpMode.Disabled, false, 12345, cancellation.Token);
        try
        {
            if (timesOut)
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.False(operation.IsCompleted);
                Assert.False(core.Stopped);
                // Fire the production deadline explicitly instead of racing two wall-clock timers under CI load.
                Assert.Single(clock.Timers).Fire();
            }
            if (callerCancels)
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
                Assert.False(core.Stopped);
                Assert.Single(store.Configuration.Requests);
                Assert.Empty(logs.Store.GetRecentLogs(5));
            }
            else
            {
                await operation.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.True(core.Stopped);
                Assert.Single(store.Configuration.Requests);
                if (outcome == "success") { Assert.Empty(logs.Store.GetRecentLogs(5)); }
                else { Assert.Equal("traffic.final_sample_unavailable", Assert.Single(logs.Store.GetRecentLogs(5)).Detail); }
            }
            Assert.Equal(failsDuringSampling || outcome == "success" ? 1 : 0, samplingCalls);
            ManualDeadlineTimer timer = Assert.Single(clock.Timers);
            Assert.Equal(TimeSpan.FromSeconds(2), timer.DueTime);
            Assert.Equal(Timeout.InfiniteTimeSpan, timer.Period);
            Assert.True(timer.IsDisposed);
        }
        finally
        {
            cancellation.Cancel();
            try { await operation.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        }
    }

    private sealed class ManualDeadlineTimeProvider : TimeProvider
    {
        public List<ManualDeadlineTimer> Timers { get; } = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            ManualDeadlineTimer timer = new(callback, state, dueTime, period);
            Timers.Add(timer);
            return timer;
        }
    }

    private sealed class ManualDeadlineTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) : ITimer
    {
        private int _disposed;
        public TimeSpan DueTime { get; } = dueTime;
        public TimeSpan Period { get; } = period;
        public bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        public void Fire()
        {
            Assert.False(IsDisposed);
            callback(state);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) => throw new NotSupportedException();
        public void Dispose() => Interlocked.Exchange(ref _disposed, 1);
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ScopeSelections : INetworkTakeoverProxySelections
    {
        public List<RuntimeConfigurationActivationPlan> Restored { get; } = [];
        public Task RestoreAsync(RuntimeConfigurationActivationPlan plan, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Restored.Add(plan);
            return Task.CompletedTask;
        }
    }

    private sealed class ScopedLogDatabase : IAsyncDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "ClashSharp-ScopedNative-" + Guid.NewGuid().ToString("N"));
        public ScopedLogDatabase()
        {
            Directory.CreateDirectory(_root);
            Store = new(Path.Combine(_root, "logs.sqlite3"), () => "profile-a");
        }
        public LogStorageService Store { get; }
        public async ValueTask DisposeAsync()
        {
            await Store.DisposeAsync();
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class ScopeConfiguration : ICoreConfigurationStore
    {
        public FakeNetworkTakeoverCoreConfiguration Configuration { get; } = new()
        {
            State = new(AppContext.BaseDirectory, typeof(NetworkTakeoverServiceTests).Assembly.Location, true) { DataGenerationId = Guid.NewGuid() },
        };
        public CoreConfigurationState GetState() => Configuration.State;
        public Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(ClashSharpMode mode, bool tun,
            int port, ICoreConfigurationRuntime runtime, CancellationToken token) => Configuration.ApplyConfigurationAsync(mode, tun, port, runtime, token);
        public CoreConfigurationState EnsureDefaultConfiguration() => throw new NotSupportedException();
        public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode) => throw new NotSupportedException();
        public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool tun) => throw new NotSupportedException();
        public CoreConfigurationState EnsureConfiguration(ClashSharpMode mode, bool tun, int port) => throw new NotSupportedException();
        public Task<ProfileImportResult> ImportProfileConfigurationAsync(string id, string name, string text, CancellationToken token) => throw new NotSupportedException();
        public string GetProfileConfigurationPath(string id) => throw new NotSupportedException();
        public bool TryReadProfileConfigurationText(string id, out string? text) => throw new NotSupportedException();
        public Task<ProfileImportResult> ValidateImportedProfileAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<RuntimeConfigurationGenerationState> GetRuntimeGenerationStateAsync(CancellationToken token) => throw new NotSupportedException();
        public RuntimeConfigurationIntegrityObservation ObserveRuntimeConfigurationIntegrity() => throw new NotSupportedException();
        public bool CanRecoverInterruptedRuntimeConfiguration(RuntimeConfigurationActivationPlan baseline, RuntimeConfigurationActivationPlan desired) => throw new NotSupportedException();
        public Task<RuntimeConfigurationTransactionResult> ApplyRuntimeConfigurationAsync(string id, ClashSharpMode mode, bool tun, int port,
            ICoreConfigurationRuntime runtime, CancellationToken token) => throw new NotSupportedException();
        public Task<string?> ReadImportedProfileConfigurationAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<bool> DeleteImportedProfileAsync(string id, CancellationToken token) => throw new NotSupportedException();
        public Task<ProfileRuntimeConfigurationTransactionResult> ImportAndApplyProfileConfigurationAsync(string id, string name, string text,
            ClashSharpMode mode, bool tun, int port, ICoreConfigurationRuntime runtime, CancellationToken token) => throw new NotSupportedException();
    }
}
