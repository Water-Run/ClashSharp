using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;
using ClashSharp.Settings;

namespace ClashSharp.ViewModel;

internal sealed partial class SettingsViewModel
{
    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetDisplayLanguageIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        AppLanguage language;
        if (index == 0) { language = AppLanguage.AutoDetect; }
        else
        {
            int languageValue = index - 1;
            if (!Enum.IsDefined((AppLanguage)languageValue)) { return false; }
            language = (AppLanguage)languageValue;
            if (language == AppLanguage.AutoDetect) { return false; }
        }
        if (DisplayLanguage == language && _settings.DisplayLanguage == language) { return false; }
        await ApplyPreferenceAsync(SettingsRegistry.Keys.DisplayLanguage, language, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetAppThemeModeIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined((AppThemeMode)index)) { return false; }
        AppThemeMode value = (AppThemeMode)index;
        await ApplyPreferenceAsync(SettingsRegistry.Keys.AppThemeMode, value, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetAppAccentColorModeIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined((AppAccentColorMode)index)) { return false; }
        AppAccentColorMode value = (AppAccentColorMode)index;
        await ApplyPreferenceAsync(SettingsRegistry.Keys.AppAccentColorMode, value, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetStartupBehaviorModeIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined((StartupBehaviorMode)index)) { return false; }
        StartupBehaviorMode value = (StartupBehaviorMode)index;
        await ApplyPreferenceAsync(SettingsRegistry.Keys.StartupBehaviorMode, value, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetCloseBehaviorModeIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined((CloseBehaviorMode)index)) { return false; }
        CloseBehaviorMode value = (CloseBehaviorMode)index;
        await ApplyPreferenceAsync(SettingsRegistry.Keys.CloseBehaviorMode, value, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetNotificationLevelIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined((NotificationLevel)index)) { return false; }
        NotificationLevel value = (NotificationLevel)index;
        await ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationLevel, value, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetMainlandChinaFeatureModeIndexAsync(int index, CancellationToken cancellationToken = default)
    {
        if (!Enum.IsDefined((MainlandChinaFeatureMode)index)) { return false; }
        MainlandChinaFeatureMode value = (MainlandChinaFeatureMode)index;
        if (value == MainlandChinaFeatureMode.AllIncludingUrlBlacklist) { value = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter; }
        await ApplyPreferenceAsync(SettingsRegistry.Keys.MainlandChinaFeatureMode, value, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetAppAccentColorValueAsync(string value, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(value)) { return false; }
        try
        {
            await ApplyPreferenceAsync(SettingsRegistry.Keys.AppAccentColorValue, value, cancellationToken);
            return true;
        }
        catch (ArgumentException exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)) { return false; }
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetCustomAppAccentColorAsync(string value, CancellationToken cancellationToken = default)
    {
        return ApplyCustomAccentColorAsync(value, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetStartupConflictCheckEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.StartupConflictCheckEnabled, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetShowStartupGuideOnStartupAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.ShowStartupGuideOnStartup, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetTriggersEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.TriggersEnabled, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetTriggerNotificationsEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.TriggerNotificationsEnabled, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetTrayUseMonochromeInactiveIconAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.TrayUseMonochromeInactiveIcon, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetCheckStaleProxyOnStartupAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.CheckStaleProxyOnStartup, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetRestoreProxyOnExitAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.RestoreProxyOnExit, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetMainlandChinaUrlBlockingEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.MainlandChinaUrlBlockingEnabled, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetNotificationEnabledAsync(bool isEnabled, CancellationToken cancellationToken = default)
    {
        return ApplyPreferenceAsync(SettingsRegistry.Keys.NotificationEnabled, isEnabled, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task SetTrayVisibleFeatureIdsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        return ApplyTrayVisibleFeatureIdsAsync(ids, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetConnectionTestUrlAsync(string value, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeConnectionTestUrl(value, out string normalized)) { return false; }
        await ApplyPreferenceAsync(SettingsRegistry.Keys.ConnectionTestProxyUrl1, normalized, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public async Task<bool> SetConnectionTestUrlsAsync(string proxyUrl1, string proxyUrl2, string directUrl, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeConnectionTestUrl(proxyUrl1, out string first)
            || !TryNormalizeConnectionTestUrl(proxyUrl2, out string second)
            || !TryNormalizeConnectionTestUrl(directUrl, out string direct)) { return false; }
        await ApplyConnectionTestUrlsAsync(first, second, direct, cancellationToken);
        return true;
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task ResetConnectionTestUrlsToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        return ApplyConnectionTestUrlsAsync(DefaultConnectionTestProxyUrl1, DefaultConnectionTestProxyUrl2, DefaultConnectionTestDirectUrl, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task ResetBasicSettingsToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        return ResetPreferenceGroupAsync(SettingsResetScope.Basic, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task ResetNotificationSettingsToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        return ResetPreferenceGroupAsync(SettingsResetScope.Notifications, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task ResetTriggerSettingsToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        return ResetPreferenceGroupAsync(SettingsResetScope.Triggers, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task ResetTraySettingsToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        return ResetPreferenceGroupAsync(SettingsResetScope.Tray, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task ResetWindowsNativeSettingsToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        return ResetPreferenceGroupAsync(SettingsResetScope.WindowsNative, cancellationToken);
    }

    /// <summary>Validates the requested preference and awaits its complete command before publishing page state.</summary>
    public Task ResetMainlandChinaSettingsToDefaultsAsync(CancellationToken cancellationToken = default)
    {
        return ResetPreferenceGroupAsync(SettingsResetScope.MainlandChina, cancellationToken);
    }
}
