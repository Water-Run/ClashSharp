/*
 * Clash Data Package Service Factory
 * Wires production dependencies for XML data package import and export
 *
 * @author: WaterRun
 * @file: Service/ClashDataPackageServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using ClashSharp.Model;

namespace ClashSharp.Service;

internal sealed partial class ClashDataPackageService
{
    /// <summary>Shared production data package service.</summary>
    /// <value>A non-null service instance wired to persistent application settings.</value>
    public static ClashDataPackageService Instance { get; } = ClashDataPackageServiceFactory.CreateDefault();
}

internal static class ClashDataPackageServiceFactory
{
    public static ClashDataPackageService CreateDefault()
    {
        return new ClashDataPackageService(
            new ClashDataPackageSettingsAdapter(AppSettingsService.Instance),
            AppDataPathService.ResolveLocalDataDirectory());
    }
}

internal sealed class ClashDataPackageSettingsAdapter : IClashDataPackageSettings
{
    private readonly AppSettingsService _settings;

    public ClashDataPackageSettingsAdapter(AppSettingsService settings)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public AppLanguage DisplayLanguage
    {
        get => _settings.DisplayLanguage;
        set => _settings.DisplayLanguage = value;
    }

    public AppThemeMode AppThemeMode
    {
        get => _settings.AppThemeMode;
        set => _settings.AppThemeMode = value;
    }

    public AppAccentColorMode AppAccentColorMode
    {
        get => _settings.AppAccentColorMode;
        set => _settings.AppAccentColorMode = value;
    }

    public string AppAccentColorValue
    {
        get => _settings.AppAccentColorValue;
        set => _settings.AppAccentColorValue = value;
    }

    public bool LaunchAtStartupEnabled
    {
        get => _settings.LaunchAtStartupEnabled;
        set => _settings.LaunchAtStartupEnabled = value;
    }

    public ClashSharpMode CurrentMode
    {
        get => _settings.CurrentMode;
        set => _settings.CurrentMode = value;
    }

    public string ActiveProfileId
    {
        get => _settings.ActiveProfileId;
        set => _settings.ActiveProfileId = value;
    }

    public bool TransparentProxyEnabled
    {
        get => _settings.TransparentProxyEnabled;
        set => _settings.TransparentProxyEnabled = value;
    }

    public int MixedPort
    {
        get => _settings.MixedPort;
        set => _settings.MixedPort = value;
    }

    public bool ConnectionSamplingEnabled
    {
        get => _settings.ConnectionSamplingEnabled;
        set => _settings.ConnectionSamplingEnabled = value;
    }

    public int ConnectionSamplingIntervalSeconds
    {
        get => _settings.ConnectionSamplingIntervalSeconds;
        set => _settings.ConnectionSamplingIntervalSeconds = value;
    }

    public bool RestoreProxyOnExit
    {
        get => _settings.RestoreProxyOnExit;
        set => _settings.RestoreProxyOnExit = value;
    }

    public bool CheckStaleProxyOnStartup
    {
        get => _settings.CheckStaleProxyOnStartup;
        set => _settings.CheckStaleProxyOnStartup = value;
    }

    public bool StartupConflictCheckEnabled
    {
        get => _settings.StartupConflictCheckEnabled;
        set => _settings.StartupConflictCheckEnabled = value;
    }

    public StartupBehaviorMode StartupBehaviorMode
    {
        get => _settings.StartupBehaviorMode;
        set => _settings.StartupBehaviorMode = value;
    }

    public bool ShowStartupGuideOnStartup
    {
        get => _settings.ShowStartupGuideOnStartup;
        set => _settings.ShowStartupGuideOnStartup = value;
    }

    public ProxyRecoveryMode ProxyRecoveryMode
    {
        get => _settings.ProxyRecoveryMode;
        set => _settings.ProxyRecoveryMode = value;
    }

    public MainlandChinaFeatureMode MainlandChinaFeatureMode
    {
        get => _settings.MainlandChinaFeatureMode;
        set => _settings.MainlandChinaFeatureMode = value;
    }

    public bool MainlandChinaUrlBlockingEnabled
    {
        get => _settings.MainlandChinaUrlBlockingEnabled;
        set => _settings.MainlandChinaUrlBlockingEnabled = value;
    }

    public string ConnectionTestUrl
    {
        get => _settings.ConnectionTestUrl;
        set => _settings.ConnectionTestUrl = value;
    }

    public string ConnectionTestProxyUrl1
    {
        get => _settings.ConnectionTestProxyUrl1;
        set => _settings.ConnectionTestProxyUrl1 = value;
    }

    public string ConnectionTestProxyUrl2
    {
        get => _settings.ConnectionTestProxyUrl2;
        set => _settings.ConnectionTestProxyUrl2 = value;
    }

    public string ConnectionTestDirectUrl
    {
        get => _settings.ConnectionTestDirectUrl;
        set => _settings.ConnectionTestDirectUrl = value;
    }
}
