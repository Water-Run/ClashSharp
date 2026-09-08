using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Checks grouped resets against ordinary and exclusive settings writers.</summary>
public sealed class AppSettingsPreferenceResetTests
{
    [Theory]
    [InlineData(SettingsResetScope.Basic)]
    [InlineData(SettingsResetScope.Notifications)]
    [InlineData(SettingsResetScope.Triggers)]
    [InlineData(SettingsResetScope.Tray)]
    [InlineData(SettingsResetScope.WindowsNative)]
    [InlineData(SettingsResetScope.MainlandChina)]
    public async Task PreferenceReset_NotifiesCompleteDefaultsWhileRetainingAdmission(SettingsResetScope scope)
    {
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        Dictionary<string, object> defaults = Snapshot(settings);
        SeedPreferences(settings);
        Dictionary<string, object> expected = Snapshot(settings);
        foreach (string key in ResetKeys(scope))
        {
            expected[key] = defaults[key];
        }

        List<Dictionary<string, object>> observations = [];
        ValueTask<MutationAdmissionLease> pendingExclusive = default;
        void Changed(object? sender, AppSettingChangedEventArgs change)
        {
            observations.Add(Snapshot(settings));
            if (observations.Count == 1)
            {
                pendingExclusive = barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            }

            Assert.False(pendingExclusive.IsCompleted);
        }

        settings.SettingChanged += Changed;
        try
        {
            settings.ResetPreferenceGroup(scope);

            await using MutationAdmissionLease exclusive = await pendingExclusive;
            Assert.True(exclusive.IsExclusive);
            Assert.NotEmpty(observations);
            Assert.All(observations, state => Assert.Equal(expected.OrderBy(pair => pair.Key), state.OrderBy(pair => pair.Key)));
        }
        finally
        {
            settings.SettingChanged -= Changed;
            settings.ResetAllSettings();
        }
    }

    [Theory]
    [InlineData(SettingsResetScope.Basic)]
    [InlineData(SettingsResetScope.Notifications)]
    [InlineData(SettingsResetScope.Triggers)]
    [InlineData(SettingsResetScope.Tray)]
    [InlineData(SettingsResetScope.WindowsNative)]
    [InlineData(SettingsResetScope.MainlandChina)]
    public async Task PreferenceReset_ExclusiveOperationRejectsTheEntireGroup(SettingsResetScope scope)
    {
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        SeedPreferences(settings);
        Dictionary<string, object> before = Snapshot(settings);
        try
        {
            await using MutationAdmissionLease exclusive = await barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            Assert.Throws<MutationAdmissionRejectedException>(() => settings.ResetPreferenceGroup(scope));
            Assert.Equal(before.OrderBy(pair => pair.Key), Snapshot(settings).OrderBy(pair => pair.Key));
        }
        finally
        {
            settings.ResetAllSettings();
        }
    }

    [Theory]
    [InlineData(SettingsResetScope.None)]
    [InlineData(SettingsResetScope.Startup)]
    [InlineData(SettingsResetScope.Proxy)]
    [InlineData(SettingsResetScope.TransparentProxy)]
    [InlineData(SettingsResetScope.All)]
    [InlineData(SettingsResetScope.Basic | SettingsResetScope.Notifications)]
    [InlineData((SettingsResetScope)(1 << 29))]
    public void PreferenceReset_RejectsUnsupportedScopesWithoutChangingSettings(SettingsResetScope scope)
    {
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(new MutationAdmissionBarrier());
        settings.ResetAllSettings();
        SeedPreferences(settings);
        Dictionary<string, object> before = Snapshot(settings);
        try
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => settings.ResetPreferenceGroup(scope));
            Assert.Equal(before.OrderBy(pair => pair.Key), Snapshot(settings).OrderBy(pair => pair.Key));
        }
        finally
        {
            settings.ResetAllSettings();
        }
    }

    private static void SeedPreferences(AppSettingsService settings)
    {
        settings.DisplayLanguage = AppLanguage.German;
        settings.AppThemeMode = AppThemeMode.Dark;
        settings.SetCustomAppAccentColor("#FF2D7D9A");
        settings.CloseBehaviorMode = CloseBehaviorMode.ConfirmExit;
        settings.NotificationEnabled = false;
        settings.NotificationLevel = NotificationLevel.CriticalOnly;
        settings.TriggersEnabled = false;
        settings.TriggerNotificationsEnabled = false;
        settings.TrayUseMonochromeInactiveIcon = true;
        settings.TrayVisibleFeatureIds = "status";
        settings.CheckStaleProxyOnStartup = false;
        settings.RestoreProxyOnExit = false;
        settings.MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled;
        settings.MainlandChinaUrlBlockingEnabled = true;
        settings.MixedPort = 23456;
        settings.TransparentProxyEnabled = false;
        settings.LaunchAtStartupEnabled = true;
    }

    private static Dictionary<string, object> Snapshot(AppSettingsService settings) => new(StringComparer.Ordinal)
    {
        ["DisplayLanguage"] = settings.DisplayLanguage,
        ["AppThemeMode"] = settings.AppThemeMode,
        ["AppAccentColorMode"] = settings.AppAccentColorMode,
        ["AppAccentColorValue"] = settings.AppAccentColorValue,
        ["CloseBehaviorMode"] = settings.CloseBehaviorMode,
        ["NotificationEnabled"] = settings.NotificationEnabled,
        ["NotificationLevel"] = settings.NotificationLevel,
        ["TriggersEnabled"] = settings.TriggersEnabled,
        ["TriggerNotificationsEnabled"] = settings.TriggerNotificationsEnabled,
        ["TrayUseMonochromeInactiveIcon"] = settings.TrayUseMonochromeInactiveIcon,
        ["TrayVisibleFeatureIds"] = settings.TrayVisibleFeatureIds,
        ["CheckStaleProxyOnStartup"] = settings.CheckStaleProxyOnStartup,
        ["RestoreProxyOnExit"] = settings.RestoreProxyOnExit,
        ["MainlandChinaFeatureMode"] = settings.MainlandChinaFeatureMode,
        ["MainlandChinaDisplayEnabled"] = settings.MainlandChinaDisplayEnabled,
        ["MainlandChinaUrlBlockingEnabled"] = settings.MainlandChinaUrlBlockingEnabled,
        ["MixedPort"] = settings.MixedPort,
        ["TransparentProxyEnabled"] = settings.TransparentProxyEnabled,
        ["LaunchAtStartupEnabled"] = settings.LaunchAtStartupEnabled,
    };

    private static string[] ResetKeys(SettingsResetScope scope) => scope switch
    {
        SettingsResetScope.Basic => ["DisplayLanguage", "AppThemeMode", "AppAccentColorMode", "AppAccentColorValue", "CloseBehaviorMode"],
        SettingsResetScope.Notifications => ["NotificationEnabled", "NotificationLevel"],
        SettingsResetScope.Triggers => ["TriggersEnabled", "TriggerNotificationsEnabled"],
        SettingsResetScope.Tray => ["TrayUseMonochromeInactiveIcon", "TrayVisibleFeatureIds"],
        SettingsResetScope.WindowsNative => ["CheckStaleProxyOnStartup", "RestoreProxyOnExit"],
        SettingsResetScope.MainlandChina => ["MainlandChinaFeatureMode", "MainlandChinaDisplayEnabled", "MainlandChinaUrlBlockingEnabled"],
        _ => throw new ArgumentOutOfRangeException(nameof(scope)),
    };
}
