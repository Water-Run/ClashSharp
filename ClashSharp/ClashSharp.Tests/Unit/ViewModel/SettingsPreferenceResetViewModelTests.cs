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
    public void PreferenceReset_WhenPersistenceFails_PreservesTheDisplayedGroup(SettingsResetScope scope)
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

        Exception? observed = Record.Exception(() => ResetPreferenceViewModel(viewModel, scope));

        Assert.Same(failure, observed);
        Assert.Equal(scope, store.LastPreferenceReset);
        Assert.Equal(before, ReadPreferenceViewModel(viewModel));
        Assert.Empty(notifications);
        Assert.Equal(0, themeApplications);
    }

    [Fact]
    public void BasicPreferenceReset_PublishesTheCompleteGroupBeforeAnyNotification()
    {
        FakeSettingsStore store = CreateModifiedPreferenceStore();
        int themeApplications = 0;
        SettingsViewModel viewModel = new(store, _ => { }, _ => themeApplications++, () => { }, _ => { });
        viewModel.Load();
        List<string?> notifications = [];
        viewModel.PropertyChanged += (_, change) =>
        {
            Assert.Equal(SettingsResetScope.Basic, store.LastPreferenceReset);
            Assert.Equal(AppLanguage.AutoDetect, viewModel.DisplayLanguage);
            Assert.Equal(AppThemeMode.FollowSystem, viewModel.AppThemeMode);
            Assert.Equal(AppAccentColorMode.FollowSystem, viewModel.AppAccentColorMode);
            Assert.Equal("#FF0078D4", viewModel.AppAccentColorValue);
            Assert.Equal(CloseBehaviorMode.MinimizeToTray, viewModel.CloseBehaviorMode);
            Assert.False(viewModel.IsCustomAccentColorSelected);
            notifications.Add(change.PropertyName);
        };

        viewModel.ResetBasicSettingsToDefaults();

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

    private static void ResetPreferenceViewModel(SettingsViewModel viewModel, SettingsResetScope scope)
    {
        switch (scope)
        {
            case SettingsResetScope.Basic: viewModel.ResetBasicSettingsToDefaults(); break;
            case SettingsResetScope.Notifications: viewModel.ResetNotificationSettingsToDefaults(); break;
            case SettingsResetScope.Triggers: viewModel.ResetTriggerSettingsToDefaults(); break;
            case SettingsResetScope.Tray: viewModel.ResetTraySettingsToDefaults(); break;
            case SettingsResetScope.WindowsNative: viewModel.ResetWindowsNativeSettingsToDefaults(); break;
            case SettingsResetScope.MainlandChina: viewModel.ResetMainlandChinaSettingsToDefaults(); break;
            default: throw new ArgumentOutOfRangeException(nameof(scope));
        }
    }
}
