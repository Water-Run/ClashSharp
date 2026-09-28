using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class MasterControlViewModelTests
{
    public static TheoryData<string, string> PreferenceTiles => new()
    {
        { "blocked-url", nameof(IMasterControlSettings.MainlandChinaUrlBlockingEnabled) },
        { "restore-proxy-on-exit", nameof(IMasterControlSettings.RestoreProxyOnExit) },
        { "stale-proxy-check", nameof(IMasterControlSettings.CheckStaleProxyOnStartup) },
        { "startup-conflict-check", nameof(IMasterControlSettings.StartupConflictCheckEnabled) },
        { "startup-guide", nameof(IMasterControlSettings.ShowStartupGuideOnStartup) },
    };

    [Fact]
    public async Task PreferenceTile_WriteFailureIsObservedWithoutEscapingTheUiClick()
    {
        IOException failure = new("storage unavailable");
        FakeMasterSettings settings = new() { PreferenceWriteFailure = failure };
        FakeApplicationErrorSink errors = new();
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, errorSink: errors);
        await viewModel.LoadAsync(CancellationToken.None);
        MasterControlInfoTileViewModel tile = viewModel.InfoTiles.Single(item => item.Id == "restore-proxy-on-exit");

        Exception? escaped = Record.Exception(() => tile.TileCommand!.Execute(null));
        Assert.Null(escaped);
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(tile.TileCommand);
        await command.ExecutionTask!.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(settings.RestoreProxyOnExit);
        Assert.True(tile.IsToggleOn);
        Assert.Equal("On", tile.Value);
        Assert.Equal("Unexpected error", viewModel.OperationErrorText);
        ApplicationError error = Assert.Single(errors.Errors);
        Assert.Same(failure, error.Exception);
        Assert.False(command.IsRunning);
        Assert.True(command.CanExecute(null));
    }

    [Theory]
    [MemberData(nameof(PreferenceTiles))]
    public async Task PreferenceTile_AwaitsBothToggleDirectionsAndRejectsDuplicateClicks(string tileId, string key)
    {
        foreach (bool initial in new[] { false, true })
        {
            TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
            FakeMasterSettings settings = new();
            typeof(FakeMasterSettings).GetProperty(key)!.SetValue(settings, initial);
            settings.ApplyChangesAsyncHandler = async (changes, token) =>
            {
                await release.Task.WaitAsync(token);
                settings.CommitPreferenceChanges(changes);
            };
            MasterControlViewModel viewModel = CreateViewModel(settings: settings);
            await viewModel.LoadAsync(CancellationToken.None);
            MasterControlInfoTileViewModel tile = viewModel.InfoTiles.Single(item => item.Id == tileId);
            AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(tile.TileCommand);

            command.Execute(null);
            Task execution = Assert.IsAssignableFrom<Task>(command.ExecutionTask);
            Assert.True(command.IsRunning);
            Assert.False(command.CanExecute(null));
            Assert.Equal(initial, tile.IsToggleOn);
            command.Execute(null);
            Assert.Same(execution, command.ExecutionTask);
            SettingValueChange change = Assert.Single(Assert.Single(settings.PreferenceWrites));
            Assert.Equal(key, change.Key.Value);
            Assert.Equal(!initial, change.Value.Get<bool>());

            release.SetResult();
            await execution.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(!initial, typeof(FakeMasterSettings).GetProperty(key)!.GetValue(settings));
            Assert.Equal(!initial, tile.IsToggleOn);
            Assert.Equal(initial ? "Off" : "On", tile.Value);
            Assert.Same(tile, viewModel.InfoTiles.Single(item => item.Id == tileId));
            Assert.False(command.IsRunning);
            Assert.True(command.CanExecute(null));
        }
    }

    [Theory]
    [MemberData(nameof(PreferenceTiles))]
    public async Task PreferenceTile_NotificationFailureReadsCommittedStateAndExplicitRetryUsesIt(string tileId, string key)
    {
        IOException failure = new("notification failed after commit");
        FakeApplicationErrorSink errors = new();
        FakeMasterSettings settings = new();
        typeof(FakeMasterSettings).GetProperty(key)!.SetValue(settings, false);
        settings.ApplyChangesAsyncHandler = (changes, _) =>
        {
            settings.CommitPreferenceChanges(changes);
            return Task.FromException(failure);
        };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, errorSink: errors);
        await viewModel.LoadAsync(CancellationToken.None);
        MasterControlInfoTileViewModel tile = viewModel.InfoTiles.Single(item => item.Id == tileId);
        AsyncRelayCommand command = Assert.IsType<AsyncRelayCommand>(tile.TileCommand);

        await command.ExecuteObservedAsync(null, CancellationToken.None);
        Assert.True(tile.IsToggleOn);
        Assert.Equal("On", tile.Value);
        Assert.Equal("Unexpected error", viewModel.OperationErrorText);
        Assert.Same(failure, Assert.Single(errors.Errors).Exception);
        Assert.Single(settings.PreferenceWrites);

        settings.ApplyChangesAsyncHandler = null;
        await command.ExecuteObservedAsync(null, CancellationToken.None);
        Assert.False(tile.IsToggleOn);
        Assert.Equal("Off", tile.Value);
        Assert.False(viewModel.HasOperationError);
        Assert.Equal(2, settings.PreferenceWrites.Count);
        Assert.Single(errors.Errors);
    }

    [Fact]
    public async Task PreferenceTile_CallerCancellationDoesNotWriteOrReportAnError()
    {
        FakeApplicationErrorSink errors = new();
        FakeMasterSettings settings = new();
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, errorSink: errors);
        await viewModel.LoadAsync(CancellationToken.None);
        AsyncRelayCommand command = PreferenceCommand(viewModel, "restore-proxy-on-exit");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await command.ExecuteObservedAsync(null, cancellation.Token);

        Assert.Empty(settings.PreferenceWrites);
        Assert.Empty(errors.Errors);
        Assert.False(viewModel.HasOperationError);
        Assert.True(settings.RestoreProxyOnExit);
    }

    [Fact]
    public async Task PreferenceTile_UnrelatedCancellationIsReportedAsFailure()
    {
        OperationCanceledException failure = new("store canceled independently");
        FakeApplicationErrorSink errors = new();
        FakeMasterSettings settings = new() { ApplyChangesAsyncHandler = (_, _) => Task.FromException(failure) };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, errorSink: errors);
        await viewModel.LoadAsync(CancellationToken.None);

        await PreferenceCommand(viewModel, "restore-proxy-on-exit").ExecuteObservedAsync(null, CancellationToken.None);

        Assert.True(viewModel.HasOperationError);
        Assert.Same(failure, Assert.Single(errors.Errors).Exception);
        Assert.True(settings.RestoreProxyOnExit);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2201:Do not raise reserved exception types", Justification = "Fault injection verifies that nested process-fatal exceptions are not converted into recoverable UI failures.")]
    public async Task PreferenceTile_ProcessFatalFailureStillPropagates()
    {
        AggregateException failure = new(new IOException("write failed"), new OutOfMemoryException());
        FakeApplicationErrorSink errors = new();
        FakeMasterSettings settings = new() { ApplyChangesAsyncHandler = (_, _) => Task.FromException(failure) };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, errorSink: errors);
        await viewModel.LoadAsync(CancellationToken.None);
        AsyncRelayCommand command = PreferenceCommand(viewModel, "restore-proxy-on-exit");

        Assert.Same(failure, await Assert.ThrowsAsync<AggregateException>(() => command.ExecuteAsync(null)));

        Assert.Empty(errors.Errors);
        Assert.False(command.IsRunning);
        Assert.True(settings.RestoreProxyOnExit);
    }

    [Fact]
    public async Task PreferenceTile_UsesTheValueCommittedByAnEarlierPageInteraction()
    {
        FakeApplicationErrorSink errors = new();
        MasterControlTileActionSession session = new(errors);
        FakeMasterSettings settings = new() { RestoreProxyOnExit = false };
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Activate(async (_, token) =>
        {
            entered.SetResult();
            await release.Task.WaitAsync(token);
            settings.RestoreProxyOnExit = true;
        });
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, errorSink: errors,
            presentTileActionAsync: session.ExecuteAsync, runTileOperationAsync: session.RunAsync);
        await viewModel.LoadAsync(CancellationToken.None);
        Task predecessor = session.ExecuteAsync(MasterControlTileAction.ImportConfiguration, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AsyncRelayCommand command = PreferenceCommand(viewModel, "restore-proxy-on-exit");

        command.Execute(null);
        Assert.True(command.IsRunning);
        Assert.Empty(settings.PreferenceWrites);
        release.SetResult();
        await Task.WhenAll(predecessor, command.ExecutionTask!).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(settings.RestoreProxyOnExit);
        Assert.False(Assert.Single(Assert.Single(settings.PreferenceWrites)).Value.Get<bool>());
        Assert.Empty(errors.Errors);
        session.Deactivate();
        await session.DrainAsync();
    }

    [Fact]
    public async Task PreferenceTile_PageExitDrainsCommittedWriteAndRevokesQueuedAndLateClicks()
    {
        FakeApplicationErrorSink errors = new();
        MasterControlTileActionSession session = new(errors);
        session.Activate((_, _) => Task.CompletedTask);
        FakeMasterSettings settings = new();
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken admittedToken = default;
        settings.ApplyChangesAsyncHandler = async (changes, token) =>
        {
            admittedToken = token;
            entered.SetResult();
            // A store that has begun publication owns the commit even after page cancellation.
            await release.Task;
            settings.CommitPreferenceChanges(changes);
        };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings, errorSink: errors,
            presentTileActionAsync: session.ExecuteAsync, runTileOperationAsync: session.RunAsync);
        await viewModel.LoadAsync(CancellationToken.None);
        AsyncRelayCommand first = PreferenceCommand(viewModel, "blocked-url");
        AsyncRelayCommand queued = PreferenceCommand(viewModel, "restore-proxy-on-exit");
        first.Execute(null);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        queued.Execute(null);

        session.Deactivate();
        await PreferenceCommand(viewModel, "startup-guide").ExecuteAsync(null);
        Task drain = session.DrainAsync();
        Assert.True(admittedToken.IsCancellationRequested);
        Assert.False(drain.IsCompleted);
        Assert.True(first.IsRunning);
        Assert.True(queued.IsRunning);
        release.SetResult();
        await Task.WhenAll(first.ExecutionTask!, queued.ExecutionTask!, drain).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(settings.PreferenceWrites);
        Assert.True(settings.MainlandChinaUrlBlockingEnabled);
        Assert.True(viewModel.InfoTiles.Single(tile => tile.Id == "blocked-url").IsToggleOn);
        Assert.True(settings.RestoreProxyOnExit);
        Assert.True(settings.ShowStartupGuideOnStartup);
        Assert.Empty(errors.Errors);
        Assert.False(first.IsRunning);
        Assert.False(queued.IsRunning);

        settings.ApplyChangesAsyncHandler = null;
        session.Activate((_, _) => Task.CompletedTask);
        await queued.ExecuteAsync(null);
        Assert.False(settings.RestoreProxyOnExit);
        Assert.Equal(2, settings.PreferenceWrites.Count);
        session.Deactivate();
        await session.DrainAsync();
    }

    [Theory]
    [InlineData("transparent-proxy")]
    [InlineData("startup-launch")]
    [InlineData("connection-sampling")]
    public async Task RuntimeSettingTile_PageExitCancelsQueuedDispatch(string tileId)
    {
        FakeApplicationErrorSink errors = new();
        MasterControlTileActionSession session = new(errors);
        TaskCompletionSource entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Activate(async (_, token) =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        });
        int calls = 0;
        FakeApplicationActionDispatcher actions = new()
        {
            OnDispatch = (_, _, _) => { calls++; return Task.CompletedTask; },
        };
        MasterControlViewModel viewModel = CreateViewModel(actions: actions, errorSink: errors,
            presentTileActionAsync: session.ExecuteAsync, runTileOperationAsync: session.RunAsync);
        await viewModel.LoadAsync(CancellationToken.None);
        Task predecessor = session.ExecuteAsync(MasterControlTileAction.ImportConfiguration, CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        AsyncRelayCommand command = PreferenceCommand(viewModel, tileId);
        command.Execute(null);

        session.Deactivate();
        await Task.WhenAll(predecessor, command.ExecutionTask!, session.DrainAsync()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(0, calls);
        Assert.Empty(errors.Errors);
        Assert.False(command.IsRunning);
    }

    private static AsyncRelayCommand PreferenceCommand(MasterControlViewModel viewModel, string tileId) =>
        Assert.IsType<AsyncRelayCommand>(viewModel.InfoTiles.Single(tile => tile.Id == tileId).TileCommand);
}
