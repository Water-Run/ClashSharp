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

    /// <summary>Verifies the startup guide prompt is enabled for first-run onboarding by default.</summary>
    [Fact]
    public void ShowStartupGuideOnStartup_DefaultsToTrue()
    {
        ResetSettings();

        Assert.True(ReadShowStartupGuideOnStartup());
    }

    /// <summary>Verifies the app follows the system display style by default.</summary>
    [Fact]
    public void AppThemeMode_DefaultsToFollowSystem()
    {
        ResetSettings();

        Assert.Equal(AppThemeMode.FollowSystem, AppSettingsService.Instance.AppThemeMode);
    }

    /// <summary>Verifies the app follows the Windows accent color by default.</summary>
    [Fact]
    public void AppAccentColorMode_DefaultsToFollowSystem()
    {
        ResetSettings();

        Assert.Equal("FollowSystem", ReadAppAccentColorModeName());
    }

    /// <summary>Verifies the custom accent color has a valid picker seed even when system accent is active.</summary>
    [Fact]
    public void AppAccentColorValue_DefaultsToWindowsBlue()
    {
        ResetSettings();

        Assert.Equal("#FF0078D4", ReadAppAccentColorValue());
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
        WriteAppAccentColorMode("Custom");
        WriteAppAccentColorValue("#FF2D7D9A");
        AppSettingsService.Instance.LaunchAtStartupEnabled = true;
        AppSettingsService.Instance.StartupBehaviorMode = StartupBehaviorMode.StartRuleProxy;
        AppSettingsService.Instance.StartupConflictCheckEnabled = false;
        WriteShowStartupGuideOnStartup(false);

        AppSettingsService.Instance.ResetAllSettings();

        Assert.Equal(10000, AppSettingsService.Instance.MixedPort);
        Assert.Equal("https://www.google.com/generate_204", AppSettingsService.Instance.ConnectionTestUrl);
        Assert.False(AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled);
        Assert.Equal(AppLanguage.AutoDetect, AppSettingsService.Instance.DisplayLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, AppSettingsService.Instance.AppThemeMode);
        Assert.Equal("FollowSystem", ReadAppAccentColorModeName());
        Assert.Equal("#FF0078D4", ReadAppAccentColorValue());
        Assert.False(AppSettingsService.Instance.LaunchAtStartupEnabled);
        Assert.Equal(StartupBehaviorMode.LastSetting, AppSettingsService.Instance.StartupBehaviorMode);
        Assert.True(AppSettingsService.Instance.StartupConflictCheckEnabled);
        Assert.True(ReadShowStartupGuideOnStartup());
    }

    /// <summary>Restores process-wide application settings before a default-value assertion.</summary>
    private static void ResetSettings()
    {
        AppSettingsService.Instance.ResetAllSettings();
    }

    /// <summary>Reads the startup guide setting by name so the red test can describe the new contract before implementation.</summary>
    /// <returns>The stored startup guide setting value.</returns>
    private static bool ReadShowStartupGuideOnStartup()
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettingsService).GetProperty("ShowStartupGuideOnStartup");
        Assert.NotNull(property);
        return Assert.IsType<bool>(property.GetValue(AppSettingsService.Instance));
    }

    /// <summary>Writes the startup guide setting by name so reset behavior can be specified before implementation.</summary>
    /// <param name="value">Value to write.</param>
    private static void WriteShowStartupGuideOnStartup(bool value)
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettingsService).GetProperty("ShowStartupGuideOnStartup");
        Assert.NotNull(property);
        property.SetValue(AppSettingsService.Instance, value);
    }

    /// <summary>Reads the accent color mode by name so the red test can describe the new contract before implementation.</summary>
    /// <returns>The enum value name.</returns>
    private static string ReadAppAccentColorModeName()
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettingsService).GetProperty("AppAccentColorMode");
        Assert.NotNull(property);
        object? value = property.GetValue(AppSettingsService.Instance);
        Assert.NotNull(value);
        return value.ToString() ?? string.Empty;
    }

    /// <summary>Writes the accent color mode by name through the future enum property.</summary>
    /// <param name="name">Enum value name.</param>
    private static void WriteAppAccentColorMode(string name)
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettingsService).GetProperty("AppAccentColorMode");
        Assert.NotNull(property);
        object value = Enum.Parse(property.PropertyType, name);
        property.SetValue(AppSettingsService.Instance, value);
    }

    /// <summary>Reads the custom accent color value through reflection.</summary>
    /// <returns>The persisted ARGB hex color.</returns>
    private static string ReadAppAccentColorValue()
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettingsService).GetProperty("AppAccentColorValue");
        Assert.NotNull(property);
        return Assert.IsType<string>(property.GetValue(AppSettingsService.Instance));
    }

    /// <summary>Writes the custom accent color value through reflection.</summary>
    /// <param name="value">ARGB hex color string.</param>
    private static void WriteAppAccentColorValue(string value)
    {
        System.Reflection.PropertyInfo? property = typeof(AppSettingsService).GetProperty("AppAccentColorValue");
        Assert.NotNull(property);
        property.SetValue(AppSettingsService.Instance, value);
    }
}
