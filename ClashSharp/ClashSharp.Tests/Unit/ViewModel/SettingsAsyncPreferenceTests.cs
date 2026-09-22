using ClashSharp.Model;
using ClashSharp.Presentation.Lifecycle;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Fact]
    public async Task PreferenceCommand_EmptyTraySelectionRetainsTheDefaultMenu()
    {
        FakeSettingsStore store = new() { TrayVisibleFeatureIds = "status" };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();

        await viewModel.ApplyTrayVisibleFeatureIdsAsync([], CancellationToken.None);

        Assert.Equal(SettingsRegistry.Default.Get(SettingsRegistry.Keys.TrayVisibleFeatureIds.Value).DefaultValue.Get<string>(), store.TrayVisibleFeatureIds);
        Assert.Equal(store.TrayVisibleFeatureIds, viewModel.TrayVisibleFeatureIds);
    }

    [Fact]
    public async Task PreferenceCommand_UrlEditorAndWriterUseTheSameValidation()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        string[] invalidUrls = ["https://name:secret@example.com", "https://example.com/" + new string('a', 2048)];
        foreach (string invalid in invalidUrls)
        {
            Assert.Equal(1, SettingsViewModel.GetInvalidConnectionTestUrlIndex("one.example", invalid, "direct.example"));
            await Assert.ThrowsAsync<ArgumentException>(() => viewModel.ApplyConnectionTestUrlsAsync(
                "one.example", invalid, "direct.example", CancellationToken.None));
        }
        Assert.Equal(SettingsViewModel.DefaultConnectionTestProxyUrl1, store.ConnectionTestProxyUrl1);
    }

    [Fact]
    public async Task PreferenceCommand_PostCommitFailureReconcilesCommittedValuesAndPreservesTheError()
    {
        IOException failure = new("notification receipt lost");
        FakeSettingsStore store = new() { AfterPreferenceCommit = () => Task.FromException(failure) };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => viewModel.ApplyCustomAccentColorAsync(
            "#2d7d9a", CancellationToken.None)));

        Assert.Equal(AppAccentColorMode.Custom, store.AppAccentColorMode);
        Assert.Equal(store.AppAccentColorMode, viewModel.AppAccentColorMode);
        Assert.Equal("#FF2D7D9A", viewModel.AppAccentColorValue);
        Assert.True(viewModel.IsAppAccentColorRestartPending);
        Assert.True(viewModel.IsPreferenceInputEnabled);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.OperationErrorText));
    }

    [Fact]
    public async Task PreferenceCommand_WaitsForPersistenceBeforePublishingTheSelection()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSettingsStore store = new() { BeforePreferenceApply = (_, token) => release.Task.WaitAsync(token) };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();

        Task operation = viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationEnabled, false, CancellationToken.None);
        Assert.False(operation.IsCompleted);
        Assert.False(viewModel.IsPreferenceInputEnabled);
        Assert.True(store.NotificationEnabled);
        Assert.True(viewModel.NotificationEnabled);
        release.SetResult();
        await operation.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(viewModel.NotificationEnabled);
        Assert.False(store.NotificationEnabled);
        Assert.True(viewModel.IsPreferenceInputEnabled);
    }

    [Fact]
    public async Task PreferenceCommand_FailureRetainsSelectionAndReportsOnlyLocalizedError()
    {
        IOException failure = new("private persistence details");
        FakeSettingsStore store = new() { BeforePreferenceApply = (_, _) => Task.FromException(failure) };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => viewModel.ApplyPreferenceAsync(
            SettingsRegistry.Keys.NotificationEnabled, false, CancellationToken.None)));

        Assert.True(store.NotificationEnabled);
        Assert.True(viewModel.NotificationEnabled);
        Assert.True(viewModel.IsPreferenceInputEnabled);
        Assert.False(string.IsNullOrWhiteSpace(viewModel.OperationErrorText));
        Assert.DoesNotContain(failure.Message, viewModel.OperationErrorText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PreferenceCommand_CancellationBeforeCommitRetainsSelectionWithoutError()
    {
        FakeSettingsStore store = new()
        {
            BeforePreferenceApply = (_, token) => Task.Delay(Timeout.InfiniteTimeSpan, token),
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();
        using CancellationTokenSource cancellation = new();

        Task operation = viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationEnabled, false, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);

        Assert.True(store.NotificationEnabled);
        Assert.True(viewModel.NotificationEnabled);
        Assert.True(viewModel.IsPreferenceInputEnabled);
        Assert.Empty(viewModel.OperationErrorText);
    }

    [Fact]
    public async Task PreferenceCommand_PageDepartureDrainsCommittedWorkAndRevokesQueuedChoices()
    {
        TaskCompletionSource committed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSettingsStore store = new()
        {
            AfterPreferenceCommit = () => { committed.SetResult(); return release.Task; },
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();
        TestApplicationErrorSink errors = new();
        PageOperationSession session = new(errors, "preferences");
        Task first = session.RunAsync(token => viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationEnabled, false, token));
        await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Task queued = session.RunAsync(token => viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationEnabled, true, token));

        session.Cancel();
        Task drain = session.DrainAsync();
        Assert.False(drain.IsCompleted);
        Assert.False(store.NotificationEnabled);
        Assert.True(viewModel.NotificationEnabled);
        release.SetResult();
        await Task.WhenAll(first, queued, drain).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(viewModel.NotificationEnabled);
        Assert.False(store.NotificationEnabled);
        Assert.True(viewModel.IsPreferenceInputEnabled);
        Assert.Empty(errors.Errors);
    }

    [Fact]
    public async Task PreferenceCommand_CopiesTheAcceptedBatchBeforeWaiting()
    {
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        FakeSettingsStore store = new() { BeforePreferenceApply = (_, token) => release.Task.WaitAsync(token) };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();
        SettingValueChange[] changes = [PreferenceChange(SettingsRegistry.Keys.NotificationEnabled, false)];

        Task operation = viewModel.ApplyPreferenceChangesAsync(changes, CancellationToken.None);
        changes[0] = PreferenceChange(SettingsRegistry.Keys.NotificationEnabled, true);
        release.SetResult();
        await operation;

        Assert.False(store.NotificationEnabled);
        Assert.False(viewModel.NotificationEnabled);
    }

    [Fact]
    public async Task PreferenceCommand_ColorPairIsNormalizedAndPublishedBeforeAnyAppearanceNotification()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();
        List<string> observed = [];
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName is nameof(SettingsViewModel.AppAccentColorMode) or nameof(SettingsViewModel.AppAccentColorValue))
            {
                Assert.Equal(AppAccentColorMode.Custom, viewModel.AppAccentColorMode);
                Assert.Equal("#FF2D7D9A", viewModel.AppAccentColorValue);
                Assert.Equal(viewModel.AppAccentColorMode, store.AppAccentColorMode);
                Assert.Equal(viewModel.AppAccentColorValue, store.AppAccentColorValue);
                observed.Add(change.PropertyName);
            }
        };

        await viewModel.ApplyCustomAccentColorAsync("  #2d7d9a  ", CancellationToken.None);

        Assert.Equal(2, observed.Count);
        Assert.True(viewModel.IsAppAccentColorRestartPending);
    }

    [Fact]
    public async Task PreferenceCommand_LaterEditsKeepTheOriginalRestartBaseline()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();

        await viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.DisplayLanguage, AppLanguage.German, CancellationToken.None);
        await viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationEnabled, false, CancellationToken.None);
        Assert.True(viewModel.IsDisplayLanguageRestartPending);
        await viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.DisplayLanguage, AppLanguage.AutoDetect, CancellationToken.None);
        Assert.False(viewModel.IsDisplayLanguageRestartPending);
    }

    [Theory]
    [InlineData(SettingsResetScope.Basic)]
    [InlineData(SettingsResetScope.Notifications)]
    [InlineData(SettingsResetScope.Triggers)]
    [InlineData(SettingsResetScope.Tray)]
    [InlineData(SettingsResetScope.WindowsNative)]
    [InlineData(SettingsResetScope.MainlandChina)]
    public async Task PreferenceCommand_ResetFailureKeepsTheCompleteDisplayedGroup(SettingsResetScope scope)
    {
        FakeSettingsStore store = CreateModifiedPreferenceStore();
        store.PreferenceResetFailure = new IOException("reset unavailable");
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();
        object[] before = ReadPreferenceViewModel(viewModel);

        await Assert.ThrowsAsync<IOException>(() => viewModel.ResetPreferenceGroupAsync(scope, CancellationToken.None));

        Assert.Equal(before, ReadPreferenceViewModel(viewModel));
        Assert.True(viewModel.IsPreferenceInputEnabled);
    }

    [Fact]
    public async Task PreferenceCommand_BasicResetPublishesTheCompleteGroupAndAppliesThemeAfterCommit()
    {
        FakeSettingsStore store = CreateModifiedPreferenceStore();
        int themeApplications = 0;
        SettingsViewModel viewModel = new(store, _ => { }, mode =>
        {
            Assert.Equal(SettingsResetScope.Basic, store.LastPreferenceReset);
            Assert.Equal(AppThemeMode.FollowSystem, mode);
            themeApplications++;
        }, () => { }, _ => { });
        viewModel.Load();
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SettingsViewModel.DisplayLanguage))
            {
                Assert.Equal(AppLanguage.AutoDetect, viewModel.DisplayLanguage);
                Assert.Equal(AppThemeMode.FollowSystem, viewModel.AppThemeMode);
                Assert.Equal(AppAccentColorMode.FollowSystem, viewModel.AppAccentColorMode);
                Assert.Equal("#FF0078D4", viewModel.AppAccentColorValue);
                Assert.Equal(CloseBehaviorMode.MinimizeToTray, viewModel.CloseBehaviorMode);
            }
        };

        await viewModel.ResetPreferenceGroupAsync(SettingsResetScope.Basic, CancellationToken.None);

        Assert.Equal(1, themeApplications);
        Assert.False(viewModel.NotificationEnabled);
        Assert.Equal(23456, viewModel.MixedPort);
        Assert.True(viewModel.IsDisplayLanguageRestartPending);
    }

    [Fact]
    public async Task PreferenceCommand_ConnectionDestinationsPublishTogetherAndRejectAnInvalidBatch()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();
        int summaryNotifications = 0;
        viewModel.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(SettingsViewModel.ConnectionTestUrlSummaryText))
            {
                Assert.Equal("https://one.example", viewModel.ConnectionTestProxyUrl1);
                Assert.Equal("https://two.example", viewModel.ConnectionTestProxyUrl2);
                Assert.Equal("https://direct.example", viewModel.ConnectionTestDirectUrl);
                summaryNotifications++;
            }
        };

        await viewModel.ApplyConnectionTestUrlsAsync(" one.example ", "https://two.example/", "direct.example", CancellationToken.None);
        Assert.Equal(1, summaryNotifications);
        await Assert.ThrowsAsync<ArgumentException>(() => viewModel.ApplyConnectionTestUrlsAsync(
            "new.example", "file:///private/path", "direct.example", CancellationToken.None));
        Assert.Equal("https://one.example", store.ConnectionTestProxyUrl1);
    }

    [Fact]
    public async Task PreferenceCommand_RejectsRuntimeKeysAndDuplicateOrWronglyTypedChangesBeforeCallingTheStore()
    {
        FakeSettingsStore store = new()
        {
            BeforePreferenceApply = (_, _) => throw new InvalidOperationException("store must not be called"),
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        SettingValueChange enabled = PreferenceChange(SettingsRegistry.Keys.NotificationEnabled, false);
        await Assert.ThrowsAsync<ArgumentException>(() => viewModel.ApplyPreferenceAsync(SettingsRegistry.Keys.MixedPort, 12345, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => viewModel.ApplyPreferenceChangesAsync([enabled, enabled], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentException>(() => viewModel.ApplyPreferenceChangesAsync(
            [new(SettingsRegistry.Keys.NotificationEnabled, PreferenceChange(SettingsRegistry.Keys.MixedPort, 12345).Value)], CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => viewModel.ResetPreferenceGroupAsync(SettingsResetScope.Startup, CancellationToken.None));
        Assert.True(viewModel.IsPreferenceInputEnabled);
    }

    private static SettingValueChange PreferenceChange<T>(SettingKey key, T value) where T : notnull =>
        new(key, SettingsRegistry.Default.Get(key.Value).NormalizeValue(value).Value!);
}
