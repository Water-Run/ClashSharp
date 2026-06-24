/*
 * App Settings Service Tests
 * Verifies default user-facing settings
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/AppSettingsServiceTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests defaults exposed by application settings.</summary>
public sealed class AppSettingsServiceTests
{
    /// <summary>Verifies the default mixed proxy port avoids common proxy/VPN defaults.</summary>
    [Fact]
    public void MixedPort_DefaultsTo10000()
    {
        ResetSettings();

        Assert.Equal(10000, AppSettingsService.Instance.MixedPort);
    }

    /// <summary>Verifies the default connection test URL matches the configured Clash# probe endpoint.</summary>
    [Fact]
    public void ConnectionTestUrl_DefaultsToGoogleGenerate204()
    {
        ResetSettings();

        Assert.Equal("https://www.google.com/generate_204", AppSettingsService.Instance.ConnectionTestUrl);
    }

    /// <summary>Verifies the default language follows the operating system.</summary>
    [Fact]
    public void DisplayLanguage_DefaultsToAutoDetect()
    {
        ResetSettings();

        Assert.Equal(AppLanguage.AutoDetect, AppSettingsService.Instance.DisplayLanguage);
    }

    /// <summary>Verifies startup behavior defaults to preserving the previous user mode.</summary>
    [Fact]
    public void StartupBehaviorMode_DefaultsToLastSetting()
    {
        ResetSettings();

        Assert.Equal(StartupBehaviorMode.LastSetting, AppSettingsService.Instance.StartupBehaviorMode);
    }

    /// <summary>Verifies startup conflict checks are enabled unless the user disables them.</summary>
    [Fact]
    public void StartupConflictCheckEnabled_DefaultsToTrue()
    {
        ResetSettings();

        Assert.True(AppSettingsService.Instance.StartupConflictCheckEnabled);
    }

    /// <summary>Verifies the app follows the system display style by default.</summary>
    [Fact]
    public void AppThemeMode_DefaultsToFollowSystem()
    {
        ResetSettings();

        Assert.Equal(AppThemeMode.FollowSystem, AppSettingsService.Instance.AppThemeMode);
    }

    /// <summary>Verifies launch-at-startup is opt-in.</summary>
    [Fact]
    public void LaunchAtStartupEnabled_DefaultsToFalse()
    {
        ResetSettings();

        Assert.False(AppSettingsService.Instance.LaunchAtStartupEnabled);
    }

    /// <summary>Verifies mainland China URL blocking is controlled independently from display mode.</summary>
    [Fact]
    public void MainlandChinaUrlBlockingEnabled_DefaultsToFalse()
    {
        ResetSettings();

        Assert.False(AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled);
    }

    /// <summary>Verifies reset clears persisted overrides back to their default values.</summary>
    [Fact]
    public void ResetAllSettings_RestoresDefaults()
    {
        AppSettingsService.Instance.MixedPort = 12000;
        AppSettingsService.Instance.ConnectionTestUrl = "https://example.com/generate_204";
        AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled = true;
        AppSettingsService.Instance.DisplayLanguage = AppLanguage.German;
        AppSettingsService.Instance.AppThemeMode = AppThemeMode.Dark;
        AppSettingsService.Instance.LaunchAtStartupEnabled = true;
        AppSettingsService.Instance.StartupBehaviorMode = StartupBehaviorMode.StartRuleProxy;
        AppSettingsService.Instance.StartupConflictCheckEnabled = false;

        AppSettingsService.Instance.ResetAllSettings();

        Assert.Equal(10000, AppSettingsService.Instance.MixedPort);
        Assert.Equal("https://www.google.com/generate_204", AppSettingsService.Instance.ConnectionTestUrl);
        Assert.False(AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled);
        Assert.Equal(AppLanguage.AutoDetect, AppSettingsService.Instance.DisplayLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, AppSettingsService.Instance.AppThemeMode);
        Assert.False(AppSettingsService.Instance.LaunchAtStartupEnabled);
        Assert.Equal(StartupBehaviorMode.LastSetting, AppSettingsService.Instance.StartupBehaviorMode);
        Assert.True(AppSettingsService.Instance.StartupConflictCheckEnabled);
    }

    /// <summary>Restores process-wide application settings before a default-value assertion.</summary>
    private static void ResetSettings()
    {
        AppSettingsService.Instance.ResetAllSettings();
    }
}
