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
        NetworkTakeoverService first = root.BindDataScope(firstStore, new ScopeSelections(), _ => Task.CompletedTask,
            async token => { entered.TrySetResult(); await release.Task.WaitAsync(token); }, logs.Store);
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
    }

    [Theory]
    [InlineData("failure")]
    [InlineData("timeout")]
    [InlineData("cancel")]
    public async Task BoundFinalSample_UsesOwnedDiagnosticsAndPreservesCallerCancellation(string outcome)
    {
        await using ScopedLogDatabase logs = new();
        using CancellationTokenSource cancellation = new();
        FakeNetworkTakeoverCore core = new();
        NetworkTakeoverService root = CreateService(core: core);
        ScopeConfiguration store = new();
        NetworkTakeoverService scoped = root.BindDataScope(store, new ScopeSelections(), async token =>
        {
            if (outcome == "cancel") { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            if (outcome == "timeout") { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            throw new IOException("sample unavailable");
        }, _ => throw new InvalidOperationException("sampling must not follow a failed refresh"), logs.Store);
        Task operation = scoped.ApplyModeAsync(ClashSharpMode.Disabled, false, 12345, cancellation.Token);
        if (outcome == "cancel")
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
            Assert.Equal("traffic.final_sample_unavailable", Assert.Single(logs.Store.GetRecentLogs(5)).Detail);
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
