using ClashSharp.Model;
using ClashSharp.Settings;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed partial class SettingsViewModelTests
{
    [Theory]
    [InlineData(SettingsResetScope.Basic)]
    [InlineData(SettingsResetScope.Notifications)]
    [InlineData(SettingsResetScope.Triggers)]
    [InlineData(SettingsResetScope.Tray)]
    [InlineData(SettingsResetScope.WindowsNative)]
    [InlineData(SettingsResetScope.MainlandChina)]
    public async Task PreferenceReset_WhenPersistenceFails_PreservesTheDisplayedGroup(SettingsResetScope scope)
    {
        IOException failure = new("preference persistence unavailable");
        FakeSettingsStore store = CreateModifiedPreferenceStore();
        store.PreferenceResetFailure = failure;
        int themeApplications = 0;
        SettingsViewModel viewModel = new(store, _ => { }, _ => themeApplications++, () => { }, _ => { });
        viewModel.Load();
        object[] before = ReadPreferenceViewModel(viewModel);
        List<string?> notifications = [];
        viewModel.PropertyChanged += (_, change) => notifications.Add(change.PropertyName);

        Exception? observed = await Record.ExceptionAsync(() => ResetPreferenceViewModelAsync(viewModel, scope));

        Assert.Same(failure, observed);
        Assert.Equal(scope, store.LastPreferenceReset);
        Assert.Equal(before, ReadPreferenceViewModel(viewModel));
        Assert.DoesNotContain(notifications, IsPreferenceNotification);
        Assert.True(viewModel.IsPreferenceInputEnabled);
        Assert.NotEmpty(viewModel.OperationErrorText);
        Assert.Equal(0, themeApplications);
    }

    [Fact]
    public async Task BasicPreferenceReset_PublishesTheCompleteGroupBeforeAnyNotification()
    {
        FakeSettingsStore store = CreateModifiedPreferenceStore();
        int themeApplications = 0;
        SettingsViewModel viewModel = new(store, _ => { }, _ => themeApplications++, () => { }, _ => { });
        viewModel.Load();
        List<string?> notifications = [];
        viewModel.PropertyChanged += (_, change) =>
        {
            if (!IsPreferenceNotification(change.PropertyName)) { return; }
            Assert.Equal(SettingsResetScope.Basic, store.LastPreferenceReset);
            Assert.Equal(AppLanguage.AutoDetect, viewModel.DisplayLanguage);
            Assert.Equal(AppThemeMode.FollowSystem, viewModel.AppThemeMode);
            Assert.Equal(AppAccentColorMode.FollowSystem, viewModel.AppAccentColorMode);
            Assert.Equal("#FF0078D4", viewModel.AppAccentColorValue);
            Assert.Equal(CloseBehaviorMode.MinimizeToTray, viewModel.CloseBehaviorMode);
            Assert.False(viewModel.IsCustomAccentColorSelected);
            notifications.Add(change.PropertyName);
        };

        await viewModel.ResetBasicSettingsToDefaultsAsync();

        Assert.Contains(nameof(SettingsViewModel.AppAccentColorValue), notifications);
        Assert.Contains(nameof(SettingsViewModel.DisplayLanguageIndex), notifications);
        Assert.Contains(nameof(SettingsViewModel.CloseBehaviorModeIndex), notifications);
        Assert.Equal(1, themeApplications);
        Assert.False(store.NotificationEnabled);
        Assert.False(store.TriggersEnabled);
        Assert.Equal(23456, store.MixedPort);
    }

    private static FakeSettingsStore CreateModifiedPreferenceStore() => new()
    {
        DisplayLanguage = AppLanguage.German,
        AppThemeMode = AppThemeMode.Dark,
        AppAccentColorMode = AppAccentColorMode.Custom,
        AppAccentColorValue = "#FF2D7D9A",
        CloseBehaviorMode = CloseBehaviorMode.ConfirmExit,
        NotificationEnabled = false,
        NotificationLevel = NotificationLevel.CriticalOnly,
        TriggersEnabled = false,
        TriggerNotificationsEnabled = false,
        TrayUseMonochromeInactiveIcon = true,
        TrayVisibleFeatureIds = "status",
        CheckStaleProxyOnStartup = false,
        RestoreProxyOnExit = false,
        MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled,
        MainlandChinaUrlBlockingEnabled = true,
        MixedPort = 23456,
    };

    private static object[] ReadPreferenceViewModel(SettingsViewModel viewModel) =>
    [
        viewModel.DisplayLanguage, viewModel.AppThemeMode, viewModel.AppAccentColorMode,
        viewModel.AppAccentColorValue, viewModel.CloseBehaviorMode, viewModel.NotificationEnabled,
        viewModel.NotificationLevel, viewModel.TriggersEnabled, viewModel.TriggerNotificationsEnabled,
        viewModel.TrayUseMonochromeInactiveIcon, viewModel.TrayVisibleFeatureIds,
        viewModel.CheckStaleProxyOnStartup, viewModel.RestoreProxyOnExit,
        viewModel.MainlandChinaFeatureMode, viewModel.MainlandChinaUrlBlockingEnabled, viewModel.MixedPort,
    ];

    private static async Task ResetPreferenceViewModelAsync(SettingsViewModel viewModel, SettingsResetScope scope)
    {
        switch (scope)
        {
            case SettingsResetScope.Basic: await viewModel.ResetBasicSettingsToDefaultsAsync(); break;
            case SettingsResetScope.Notifications: await viewModel.ResetNotificationSettingsToDefaultsAsync(); break;
            case SettingsResetScope.Triggers: await viewModel.ResetTriggerSettingsToDefaultsAsync(); break;
            case SettingsResetScope.Tray: await viewModel.ResetTraySettingsToDefaultsAsync(); break;
            case SettingsResetScope.WindowsNative: await viewModel.ResetWindowsNativeSettingsToDefaultsAsync(); break;
            case SettingsResetScope.MainlandChina: await viewModel.ResetMainlandChinaSettingsToDefaultsAsync(); break;
            default: throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }

    private static bool IsPreferenceNotification(string? property) =>
        property is not (nameof(SettingsViewModel.IsPreferenceInputEnabled)
            or nameof(SettingsViewModel.OperationErrorText) or nameof(SettingsViewModel.HasOperationError));
}
