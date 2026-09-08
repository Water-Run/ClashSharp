using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReset_WaitsForRegistrationThenPublishesTheWholeGroup(bool cancelAfterCommit)
    {
        FakeSettingsStore store = CreateStartupResetStore();
        StartupResetState before = StartupResetState.Capture(store);
        using CancellationTokenSource cancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupResetRuntimeScope scope = new(store)
        {
            Apply = async (enabled, token) =>
            {
                Assert.False(enabled);
                Assert.False(token.CanBeCanceled);
                Assert.Equal(StartupResetState.Defaults, StartupResetState.Capture(store));
                if (cancelAfterCommit)
                {
                    cancellation.Cancel();
                }

                entered.SetResult();
                await release.Task;
            },
        };
        SettingsViewModel viewModel = CreateStartupResetViewModel(store, scope);
        viewModel.SetDisplayLanguageIndex(0);
        Assert.True(viewModel.IsDisplayLanguageRestartPending);
        List<StartupResetState> observations = [];
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(SettingsViewModel.LaunchAtStartupEnabled)
                or nameof(SettingsViewModel.StartupConflictCheckEnabled)
                or nameof(SettingsViewModel.ShowStartupGuideOnStartup)
                or nameof(SettingsViewModel.StartupBehaviorMode))
            {
                observations.Add(StartupResetState.Capture(viewModel));
            }
        };

        Task reset = viewModel.ResetStartupSettingsToDefaultsAsync(cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(reset.IsCompleted);
            Assert.False(scope.Disposed);
            Assert.Equal(before, StartupResetState.Capture(viewModel));
            Assert.Empty(observations);
        }
        finally
        {
            release.TrySetResult();
            await reset.WaitAsync(TimeSpan.FromSeconds(5));
        }

        Assert.Equal(StartupResetState.Defaults, StartupResetState.Capture(viewModel));
        Assert.Equal(4, observations.Count);
        Assert.All(observations, state => Assert.Equal(StartupResetState.Defaults, state));
        Assert.True(viewModel.IsDisplayLanguageRestartPending);
        Assert.Equal(AppLanguage.AutoDetect, store.DisplayLanguage);
        Assert.Equal(AppThemeMode.Dark, viewModel.AppThemeMode);
        Assert.Equal(7890, viewModel.MixedPort);
        Assert.Equal("profile-before-reset", store.ActiveProfileId);
        Assert.Equal(ClashSharpMode.RuleTakeover, store.CurrentMode);
        Assert.False(viewModel.ConnectionSamplingEnabled);
        Assert.Equal([false], scope.Applied);
        Assert.Equal(1, scope.Receipt!.CommitCalls);
        Assert.Equal(0, scope.Receipt.RollbackCalls);
        Assert.True(scope.Disposed);
        Assert.False(viewModel.IsResetRecoveryRequired);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReset_RegistrationFailureRestoresTheGroupAndReportsCompensationFailure(bool compensationFails)
    {
        FakeSettingsStore store = CreateStartupResetStore();
        StartupResetState before = StartupResetState.Capture(store);
        int restarts = 0;
        StartupResetRuntimeScope scope = new(store)
        {
            Apply = (enabled, _) => enabled && !compensationFails
                ? Task.CompletedTask
                : Task.FromException(new IOException("Injected registration failure.")),
        };
        SettingsViewModel viewModel = CreateStartupResetViewModel(store, scope, () => { restarts++; return true; });

        Exception? failure = await Record.ExceptionAsync(
            () => viewModel.ResetStartupSettingsToDefaultsAsync(CancellationToken.None));

        Assert.NotNull(failure);
        if (compensationFails)
        {
            Assert.IsType<AggregateException>(failure);
        }
        else
        {
            Assert.IsType<IOException>(failure);
        }

        Assert.Equal(before, StartupResetState.Capture(store));
        Assert.Equal(before, StartupResetState.Capture(viewModel));
        Assert.Equal([false, true], scope.Applied);
        Assert.Equal(0, scope.Receipt!.CommitCalls);
        Assert.Equal(1, scope.Receipt.RollbackCalls);
        Assert.Equal(compensationFails, viewModel.IsResetRecoveryRequired);
        Assert.Equal(compensationFails ? 1 : 0, restarts);
        Assert.Equal("Application.UnexpectedError", viewModel.OperationErrorText);
        Assert.True(scope.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReset_CancellationBeforeCommitLeavesEverySettingAndRegistrationUntouched(bool cancelDuringAdmission)
    {
        FakeSettingsStore store = CreateStartupResetStore();
        StartupResetState before = StartupResetState.Capture(store);
        using CancellationTokenSource cancellation = new();
        StartupResetRuntimeScope scope = new(store);
        int admissions = 0;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store, _ => throw new InvalidOperationException(), () => throw new InvalidOperationException(), () => { },
            beginDestructiveRuntimeMutationAsync: _ =>
            {
                admissions++;
                cancellation.Cancel();
                return ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(scope);
            });
        if (!cancelDuringAdmission)
        {
            cancellation.Cancel();
        }

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.ResetStartupSettingsToDefaultsAsync(cancellation.Token));

        Assert.Equal(before, StartupResetState.Capture(store));
        Assert.Equal(before, StartupResetState.Capture(viewModel));
        Assert.Null(scope.Receipt);
        Assert.Empty(scope.Applied);
        Assert.Equal(cancelDuringAdmission ? 1 : 0, admissions);
        Assert.Equal(cancelDuringAdmission, scope.Disposed);
    }

    [Fact]
    public async Task StartupReset_DrainsAcceptedRegistrationBeforeTakingTheResetSnapshot()
    {
        FakeSettingsStore store = CreateStartupResetStore();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupResetRuntimeScope scope = new(store);
        int admissions = 0;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store, _ => { }, () => throw new InvalidOperationException(), () => { },
            applyLaunchAtStartupAsync: async (_, _) =>
            {
                entered.SetResult();
                await release.Task;
            },
            beginDestructiveRuntimeMutationAsync: _ =>
            {
                admissions++;
                return ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(scope);
            });
        viewModel.SetLaunchAtStartupEnabled(false);
        Task accepted = viewModel.ApplyLaunchAtStartupCommand.ExecutionTask!;
        Task reset = viewModel.ResetStartupSettingsToDefaultsAsync(CancellationToken.None);
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
        Assert.Equal(StartupResetState.Defaults, StartupResetState.Capture(viewModel));
        Assert.Equal([false], scope.Applied);
        Assert.True(scope.Disposed);
    }

    [Fact]
    public async Task StartupReset_SerializesWithFullResetAndDrainsBeforeScopeDisposal()
    {
        FakeSettingsStore store = CreateStartupResetStore();
        using CancellationTokenSource queuedCancellation = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupResetRuntimeScope scope = new(store)
        {
            Apply = async (_, _) => { entered.SetResult(); await release.Task; },
        };
        int admissions = 0;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store, _ => { }, () => throw new InvalidOperationException(), () => { },
            beginDestructiveRuntimeMutationAsync: _ =>
            {
                admissions++;
                return ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(scope);
            });
        Task reset = viewModel.ResetStartupSettingsToDefaultsAsync(CancellationToken.None);
        Task queued = viewModel.ResetAllSettingsAsync(queuedCancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, admissions);
            Assert.False(queued.IsCompleted);
            queuedCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued);
            Assert.False(scope.Disposed);
        }
        finally
        {
            queuedCancellation.Cancel();
            release.TrySetResult();
            await reset.WaitAsync(TimeSpan.FromSeconds(5));
            _ = await Record.ExceptionAsync(() => queued);
        }

        Assert.Equal(1, admissions);
        Assert.True(scope.Disposed);
    }

    [Fact]
    public async Task StartupReset_CommitCleanupFailureKeepsTheActivatedDecision()
    {
        FakeSettingsStore store = CreateStartupResetStore();
        StartupResetRuntimeScope scope = new(store) { CommitFailure = new IOException("Injected cleanup failure.") };
        SettingsViewModel viewModel = CreateStartupResetViewModel(store, scope);

        await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.ResetStartupSettingsToDefaultsAsync(CancellationToken.None));

        Assert.Equal(StartupResetState.Defaults, StartupResetState.Capture(store));
        Assert.Equal(StartupResetState.Defaults, StartupResetState.Capture(viewModel));
        Assert.Equal(2, scope.Receipt!.CommitCalls);
        Assert.Equal(0, scope.Receipt.RollbackCalls);
        Assert.Equal([false], scope.Applied);
        Assert.True(scope.Disposed);
    }

    private static FakeSettingsStore CreateStartupResetStore()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        store.StartupConflictCheckEnabled = false;
        store.ShowStartupGuideOnStartup = false;
        store.StartupBehaviorMode = StartupBehaviorMode.DisableProxy;
        return store;
    }

    private static SettingsViewModel CreateStartupResetViewModel(
        FakeSettingsStore store,
        StartupResetRuntimeScope scope,
        Func<bool>? requestRestart = null)
    {
        return CreateMaintenanceViewModel(
            store, _ => throw new InvalidOperationException("Unexpected language application."),
            () => throw new InvalidOperationException("Unexpected full reset."), () => { },
            applyTheme: _ => throw new InvalidOperationException("Unexpected theme application."),
            applyAccentColor: (_, _) => throw new InvalidOperationException("Unexpected accent application."),
            requestResetRecoveryRestart: requestRestart,
            beginDestructiveRuntimeMutationAsync: _ => ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(scope));
    }

    private sealed record StartupResetState(bool Launch, bool ConflictCheck, bool Guide, StartupBehaviorMode Behavior)
    {
        public static StartupResetState Defaults { get; } = new(false, true, true, StartupBehaviorMode.LastSetting);

        public static StartupResetState Capture(FakeSettingsStore store) => new(
            store.LaunchAtStartupEnabled, store.StartupConflictCheckEnabled,
            store.ShowStartupGuideOnStartup, store.StartupBehaviorMode);

        public static StartupResetState Capture(SettingsViewModel viewModel) => new(
            viewModel.LaunchAtStartupEnabled, viewModel.StartupConflictCheckEnabled,
            viewModel.ShowStartupGuideOnStartup, viewModel.StartupBehaviorMode);

        public void Restore(FakeSettingsStore store)
        {
            store.LaunchAtStartupEnabled = Launch;
            store.StartupConflictCheckEnabled = ConflictCheck;
            store.ShowStartupGuideOnStartup = Guide;
            store.StartupBehaviorMode = Behavior;
        }
    }

    private sealed class StartupResetRuntimeScope(FakeSettingsStore store) : ISettingsDestructiveRuntimeScope
    {
        public Func<bool, CancellationToken, Task> Apply { get; init; } = (_, _) => Task.CompletedTask;
        public Exception? CommitFailure { get; init; }
        public TrackingSettingsResetReceipt? Receipt { get; private set; }
        public List<bool> Applied { get; } = [];
        public bool Disposed { get; private set; }

        public ISettingsResetTransactionReceipt BeginResetStartupSettings()
        {
            Assert.False(Disposed);
            Assert.Null(Receipt);
            StartupResetState baseline = StartupResetState.Capture(store);
            StartupResetState.Defaults.Restore(store);
            return Receipt = new TrackingSettingsResetReceipt(() => baseline.Restore(store), CommitFailure);
        }

        public ISettingsResetTransactionReceipt BeginResetSettings() => throw new InvalidOperationException("Unexpected full reset.");

        public ISettingsResetTransactionReceipt BeginResetNetworkSettings(SettingsResetScope scope, bool transparentProxyEnabled)
            => throw new InvalidOperationException("Unexpected network reset.");

        public Task<ISettingsDataPackageTransactionReceipt> BeginImportAsync(string packagePath, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected import.");

        public void RestoreDurableSettings(SettingsExternalDurableSnapshot snapshot)
            => throw new InvalidOperationException("The retained receipt owns startup rollback.");

        public Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken)
        {
            Assert.False(Disposed);
            Assert.False(cancellationToken.CanBeCanceled);
            Applied.Add(isEnabled);
            return Apply(isEnabled, cancellationToken);
        }

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected sampling restart.");

        public Task ApplyNetworkSettingsAsync(bool transparentProxyEnabled, int mixedPort, CancellationToken cancellationToken)
            => throw new InvalidOperationException("Unexpected network operation.");

        public ValueTask DisposeAsync()
        {
            Assert.False(Disposed);
            Assert.True(Receipt?.Disposed ?? true);
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
