using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Theory]
    [InlineData(SettingsResetScope.Proxy, false, false)]
    [InlineData(SettingsResetScope.Proxy, true, false)]
    [InlineData(SettingsResetScope.Proxy, false, true)]
    [InlineData(SettingsResetScope.Proxy, true, true)]
    [InlineData(SettingsResetScope.TransparentProxy, false, false)]
    [InlineData(SettingsResetScope.TransparentProxy, true, false)]
    [InlineData(SettingsResetScope.TransparentProxy, false, true)]
    [InlineData(SettingsResetScope.TransparentProxy, true, true)]
    public async Task NetworkReset_AwaitsSupportedDefaultsAndPublishesOnlyTheCompleteSelectedGroup(
        SettingsResetScope selectedScope,
        bool serviceAvailable,
        bool cancelAfterCommit)
    {
        FakeSettingsStore store = CreateNetworkResetStore(!serviceAvailable);
        NetworkResetState before = NetworkResetState.Capture(store);
        NetworkResetState expected = before.Reset(selectedScope, serviceAvailable);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkResetRuntimeScope scope = new(store, selectedScope)
        {
            Network = async (_, _, token) =>
            {
                Assert.False(token.CanBeCanceled);
                Assert.Equal(expected, NetworkResetState.Capture(store));
                if (cancelAfterCommit)
                {
                    cancellation.Cancel();
                }

                entered.SetResult();
                await release.Task;
            },
        };
        SettingsViewModel viewModel = CreateNetworkResetViewModel(store, scope, serviceAvailable);
        viewModel.SetDisplayLanguageIndex(0);
        Assert.True(viewModel.IsDisplayLanguageRestartPending);
        List<NetworkResetState> observations = [];
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(SettingsViewModel.TransparentProxyEnabled)
                or nameof(SettingsViewModel.MixedPort)
                or nameof(SettingsViewModel.ConnectionSamplingEnabled)
                or nameof(SettingsViewModel.ConnectionTestDirectUrl))
            {
                observations.Add(NetworkResetState.Capture(viewModel));
            }
        };

        Task reset = ResetNetworkGroupAsync(viewModel, selectedScope, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reset.IsCompleted);
            Assert.False(scope.Disposed);
            Assert.Equal(before, NetworkResetState.Capture(viewModel));
            Assert.Empty(observations);
        }
        finally
        {
            release.TrySetResult();
            await reset.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(expected, NetworkResetState.Capture(viewModel));
        Assert.Equal(selectedScope == SettingsResetScope.Proxy ? 4 : 1, observations.Count);
        Assert.All(observations, state => Assert.Equal(expected, state));
        Assert.Equal([(expected.Tun, expected.Port)], scope.NetworkCalls);
        Assert.Equal(selectedScope == SettingsResetScope.Proxy ? [expected] : [], scope.SamplingCalls);
        Assert.Equal(1, scope.Receipt!.CommitCalls);
        Assert.Equal(0, scope.Receipt.RollbackCalls);
        Assert.True(viewModel.IsDisplayLanguageRestartPending);
        Assert.True(viewModel.LaunchAtStartupEnabled);
        Assert.Equal(ClashSharpMode.RuleTakeover, store.CurrentMode);
        Assert.Equal("profile-before-reset", store.ActiveProfileId);
        Assert.True(scope.Disposed);
    }

    [Theory]
    [InlineData(SettingsResetScope.Proxy, "sampling", false)]
    [InlineData(SettingsResetScope.Proxy, "sampling", true)]
    [InlineData(SettingsResetScope.Proxy, "network", false)]
    [InlineData(SettingsResetScope.Proxy, "network", true)]
    [InlineData(SettingsResetScope.TransparentProxy, "network", false)]
    [InlineData(SettingsResetScope.TransparentProxy, "network", true)]
    public async Task NetworkReset_FailedParticipantRestoresTheWholeGroupAndCompensatesEverySelectedParticipant(
        SettingsResetScope selectedScope,
        string failedParticipant,
        bool compensationFails)
    {
        FakeSettingsStore store = CreateNetworkResetStore(false);
        NetworkResetState before = NetworkResetState.Capture(store);
        NetworkResetState desired = before.Reset(selectedScope, true);
        int restarts = 0;
        Task Apply(string participant)
        {
            bool activating = NetworkResetState.Capture(store) == desired;
            return participant == failedParticipant && (activating || compensationFails)
                ? Task.FromException(new IOException("Injected runtime participant failure."))
                : Task.CompletedTask;
        }

        NetworkResetRuntimeScope scope = new(store, selectedScope)
        {
            Sampling = _ => Apply("sampling"),
            Network = (_, _, _) => Apply("network"),
        };
        SettingsViewModel viewModel = CreateNetworkResetViewModel(
            store, scope, true, () => { restarts++; return true; });

        Exception? failure = await Record.ExceptionAsync(
            () => ResetNetworkGroupAsync(viewModel, selectedScope, CancellationToken.None));

        Assert.NotNull(failure);
        if (compensationFails)
        {
            Assert.IsType<AggregateException>(failure);
        }
        else
        {
            Assert.IsType<IOException>(failure);
        }

        Assert.Equal(before, NetworkResetState.Capture(store));
        Assert.Equal(before, NetworkResetState.Capture(viewModel));
        Assert.Equal([(desired.Tun, desired.Port), (before.Tun, before.Port)], scope.NetworkCalls);
        Assert.Equal(selectedScope == SettingsResetScope.Proxy ? [desired, before] : [], scope.SamplingCalls);
        Assert.Equal(0, scope.Receipt!.CommitCalls);
        Assert.Equal(1, scope.Receipt.RollbackCalls);
        Assert.Equal(compensationFails, viewModel.IsResetRecoveryRequired);
        Assert.Equal(compensationFails ? 1 : 0, restarts);
        Assert.Equal("Application.UnexpectedError", viewModel.OperationErrorText);
        Assert.True(scope.Disposed);
    }

    [Theory]
    [InlineData(SettingsResetScope.Proxy)]
    [InlineData(SettingsResetScope.TransparentProxy)]
    public async Task NetworkReset_DrainsAnAcceptedNetworkRequestBeforeCapturingItsBaseline(SettingsResetScope selectedScope)
    {
        FakeSettingsStore store = CreateNetworkResetStore(false);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NetworkResetRuntimeScope scope = new(store, selectedScope);
        int admissions = 0;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store, _ => { }, () => throw new InvalidOperationException(), () => { },
            applyNetworkSettingsAsync: async (tun, port, _) =>
            {
                entered.SetResult();
                await release.Task;
                store.TransparentProxyEnabled = tun;
                store.MixedPort = port;
            },
            beginDestructiveRuntimeMutationAsync: _ =>
            {
                admissions++;
                return ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(scope);
            });
        viewModel.MixedPortValue = 23456;
        Task accepted = viewModel.ApplyNetworkSettingsCommand.ExecutionTask!;
        Task reset = ResetNetworkGroupAsync(viewModel, selectedScope, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, admissions);
            Assert.Null(scope.Receipt);
            Assert.False(reset.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await Task.WhenAll(accepted, reset).WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(1, admissions);
        Assert.Equal(selectedScope == SettingsResetScope.Proxy ? 10000 : 23456, viewModel.MixedPort);
        Assert.Equal([(true, viewModel.MixedPort)], scope.NetworkCalls);
        Assert.True(scope.Disposed);
    }

    private static Task ResetNetworkGroupAsync(
        SettingsViewModel viewModel,
        SettingsResetScope scope,
        CancellationToken cancellationToken)
    {
        return scope == SettingsResetScope.Proxy
            ? viewModel.ResetProxySettingsToDefaultsAsync(cancellationToken)
            : viewModel.ResetTransparentProxySettingsToDefaultsAsync(cancellationToken);
    }

    private static FakeSettingsStore CreateNetworkResetStore(bool tunEnabled)
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        store.TransparentProxyEnabled = tunEnabled;
        store.ConnectionTestUrl = "https://example.invalid/old-test";
        store.ConnectionTestProxyUrl1 = "https://example.invalid/old-proxy-one";
        store.ConnectionTestProxyUrl2 = "https://example.invalid/old-proxy-two";
        store.ConnectionTestDirectUrl = "https://example.invalid/old-direct";
        return store;
    }

    private static SettingsViewModel CreateNetworkResetViewModel(
        FakeSettingsStore store,
        NetworkResetRuntimeScope scope,
        bool serviceAvailable,
        Func<bool>? requestRestart = null)
    {
        return CreateMaintenanceViewModel(
            store, _ => throw new InvalidOperationException("Unexpected language application."),
            () => throw new InvalidOperationException("Unexpected full reset."), () => { },
            applyTheme: _ => throw new InvalidOperationException("Unexpected theme application."),
            applyAccentColor: (_, _) => throw new InvalidOperationException("Unexpected accent application."),
            requestResetRecoveryRestart: requestRestart,
            beginDestructiveRuntimeMutationAsync: _ => ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(scope),
            mihomoServiceController: new FakeMihomoServiceController(new MihomoServiceStatus(serviceAvailable, serviceAvailable, "Fixture status")));
    }

    private sealed record NetworkResetState(
        bool Tun, int Port, bool Sampling, int Interval, string TestUrl, string ProxyOne, string ProxyTwo, string Direct)
    {
        public static NetworkResetState Capture(FakeSettingsStore store) => new(
            store.TransparentProxyEnabled, store.MixedPort, store.ConnectionSamplingEnabled, store.ConnectionSamplingIntervalSeconds,
            store.ConnectionTestUrl, store.ConnectionTestProxyUrl1, store.ConnectionTestProxyUrl2, store.ConnectionTestDirectUrl);

        public static NetworkResetState Capture(SettingsViewModel viewModel) => new(
            viewModel.TransparentProxyEnabled, viewModel.MixedPort, viewModel.ConnectionSamplingEnabled, viewModel.ConnectionSamplingIntervalSeconds,
            viewModel.ConnectionTestUrl, viewModel.ConnectionTestProxyUrl1, viewModel.ConnectionTestProxyUrl2, viewModel.ConnectionTestDirectUrl);

        public NetworkResetState Reset(SettingsResetScope scope, bool tunEnabled) => scope == SettingsResetScope.Proxy
            ? new(tunEnabled, 10000, true, 30, "https://www.google.com/generate_204", "https://www.google.com", "https://github.com", "https://www.baidu.com")
            : this with { Tun = tunEnabled };

        public void Restore(FakeSettingsStore store)
        {
            store.TransparentProxyEnabled = Tun;
            store.MixedPort = Port;
            store.ConnectionSamplingEnabled = Sampling;
            store.ConnectionSamplingIntervalSeconds = Interval;
            store.ConnectionTestUrl = TestUrl;
            store.ConnectionTestProxyUrl1 = ProxyOne;
            store.ConnectionTestProxyUrl2 = ProxyTwo;
            store.ConnectionTestDirectUrl = Direct;
        }
    }

    private sealed class NetworkResetRuntimeScope(FakeSettingsStore store, SettingsResetScope expectedScope)
        : ISettingsDestructiveRuntimeScope
    {
        public Func<CancellationToken, Task> Sampling { get; init; } = _ => Task.CompletedTask;
        public Func<bool, int, CancellationToken, Task> Network { get; init; } = (_, _, _) => Task.CompletedTask;
        public TrackingSettingsResetReceipt? Receipt { get; private set; }
        public List<(bool Tun, int Port)> NetworkCalls { get; } = [];
        public List<NetworkResetState> SamplingCalls { get; } = [];
        public bool Disposed { get; private set; }

        public ISettingsResetTransactionReceipt BeginResetNetworkSettings(SettingsResetScope scope, bool transparentProxyEnabled)
        {
            Assert.False(Disposed);
            Assert.Null(Receipt);
            Assert.Equal(expectedScope, scope);
            NetworkResetState before = NetworkResetState.Capture(store);
            before.Reset(scope, transparentProxyEnabled).Restore(store);
            return Receipt = new TrackingSettingsResetReceipt(() => before.Restore(store));
        }

        public ISettingsResetTransactionReceipt BeginResetStartupSettings() => throw new InvalidOperationException("Unexpected startup reset.");
        public ISettingsResetTransactionReceipt BeginResetSettings() => throw new InvalidOperationException("Unexpected full reset.");
        public Task<ISettingsDataPackageTransactionReceipt> BeginImportAsync(string packagePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected import.");
        public void RestoreDurableSettings(SettingsExternalDurableSnapshot snapshot)
            => throw new InvalidOperationException("The retained receipt owns rollback.");
        public Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected startup registration.");

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken)
        {
            Assert.False(Disposed);
            Assert.False(cancellationToken.CanBeCanceled);
            Assert.Equal(SettingsResetScope.Proxy, expectedScope);
            SamplingCalls.Add(NetworkResetState.Capture(store));
            return Sampling(cancellationToken);
        }

        public Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken)
        {
            Assert.False(Disposed);
            Assert.False(cancellationToken.CanBeCanceled);
            Assert.Equal(store.TransparentProxyEnabled, transparentProxyEnabled);
            Assert.Equal(store.MixedPort, mixedPort);
            NetworkCalls.Add((transparentProxyEnabled, mixedPort));
            return Network(transparentProxyEnabled, mixedPort, cancellationToken);
        }

        public ValueTask DisposeAsync()
        {
            Assert.False(Disposed);
            Assert.True(Receipt?.Disposed ?? true);
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
