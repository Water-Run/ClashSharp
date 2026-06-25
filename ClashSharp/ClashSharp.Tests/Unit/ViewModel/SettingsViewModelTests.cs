/*
 * Settings ViewModel Tests
 * Verifies the settings view model owns settings state transitions without WinUI controls
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/SettingsViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;
using System.Net.Http;
using System.Reflection;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for settings state loading and persistence behavior.</summary>
public sealed class SettingsViewModelTests
{
    /// <summary>Verifies persisted settings are loaded into the view model snapshot.</summary>
    [Fact]
    public void Load_CopiesPersistedSettingsIntoProperties()
    {
        FakeSettingsStore store = new()
        {
            DisplayLanguage = AppLanguage.French,
            AppThemeMode = AppThemeMode.Dark,
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF2D7D9A",
            LaunchAtStartupEnabled = true,
            TransparentProxyEnabled = false,
            MixedPort = 10990,
            ConnectionSamplingEnabled = false,
            ConnectionSamplingIntervalSeconds = 45,
            StartupConflictCheckEnabled = false,
            StartupBehaviorMode = StartupBehaviorMode.DisableProxy,
            CheckStaleProxyOnStartup = false,
            RestoreProxyOnExit = false,
            ProxyRecoveryMode = ProxyRecoveryMode.EnableProxy,
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.AllIncludingUrlBlacklist,
            MainlandChinaUrlBlockingEnabled = true,
            ConnectionTestUrl = "https://example.com/generate_204",
            ConnectionTestProxyUrl1 = "https://proxy-one.example",
            ConnectionTestProxyUrl2 = "https://proxy-two.example",
            ConnectionTestDirectUrl = "https://direct.example",
        };

        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        viewModel.Load();

        Assert.Equal(AppLanguage.French, viewModel.DisplayLanguage);
        Assert.Equal((int)AppLanguage.French + 1, viewModel.DisplayLanguageIndex);
        Assert.Equal(AppThemeMode.Dark, viewModel.AppThemeMode);
        Assert.Equal((int)AppThemeMode.Dark, viewModel.AppThemeModeIndex);
        Assert.Equal(AppAccentColorMode.Custom, viewModel.AppAccentColorMode);
        Assert.Equal((int)AppAccentColorMode.Custom, viewModel.AppAccentColorModeIndex);
        Assert.Equal("#FF2D7D9A", viewModel.AppAccentColorValue);
        Assert.True(viewModel.IsCustomAccentColorSelected);
        Assert.True(viewModel.LaunchAtStartupEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.True(viewModel.CanToggleTransparentProxy);
        Assert.Equal(10990, viewModel.MixedPort);
        Assert.False(viewModel.ConnectionSamplingEnabled);
        Assert.Equal(45, viewModel.ConnectionSamplingIntervalSeconds);
        Assert.False(viewModel.StartupConflictCheckEnabled);
        Assert.Equal(StartupBehaviorMode.DisableProxy, viewModel.StartupBehaviorMode);
        Assert.Equal((int)StartupBehaviorMode.DisableProxy, viewModel.StartupBehaviorModeIndex);
        Assert.False(viewModel.CheckStaleProxyOnStartup);
        Assert.False(viewModel.RestoreProxyOnExit);
        Assert.Equal(ProxyRecoveryMode.EnableProxy, viewModel.ProxyRecoveryMode);
        Assert.Equal((int)ProxyRecoveryMode.EnableProxy, viewModel.ProxyRecoveryModeIndex);
        Assert.Equal(MainlandChinaFeatureMode.AllIncludingUrlBlacklist, viewModel.MainlandChinaFeatureMode);
        Assert.Equal((int)MainlandChinaFeatureMode.AllIncludingUrlBlacklist, viewModel.MainlandChinaFeatureModeIndex);
        Assert.True(viewModel.MainlandChinaUrlBlockingEnabled);
        Assert.Equal("https://example.com/generate_204", viewModel.ConnectionTestUrl);
        Assert.Equal("https://proxy-one.example", ReadProperty<string>(viewModel, "ConnectionTestProxyUrl1"));
        Assert.Equal("https://proxy-two.example", ReadProperty<string>(viewModel, "ConnectionTestProxyUrl2"));
        Assert.Equal("https://direct.example", ReadProperty<string>(viewModel, "ConnectionTestDirectUrl"));
    }

    /// <summary>Verifies language selection persists and notifies the shell language controller.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_ValidIndex_PersistsAndNotifiesLanguageChange()
    {
        FakeSettingsStore store = new();
        AppLanguage? notifiedLanguage = null;
        SettingsViewModel viewModel = new(store, language => notifiedLanguage = language, () => { });

        bool changed = viewModel.SetDisplayLanguageIndex((int)AppLanguage.German + 1);

        Assert.True(changed);
        Assert.Equal(AppLanguage.German, store.DisplayLanguage);
        Assert.Equal(AppLanguage.German, viewModel.DisplayLanguage);
        Assert.Equal(AppLanguage.German, notifiedLanguage);
    }

    /// <summary>Verifies the first language option stores automatic detection.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_Zero_PersistsAutoDetect()
    {
        FakeSettingsStore store = new() { DisplayLanguage = AppLanguage.English };
        AppLanguage? notifiedLanguage = null;
        SettingsViewModel viewModel = new(store, language => notifiedLanguage = language, () => { });

        bool changed = viewModel.SetDisplayLanguageIndex(0);

        Assert.True(changed);
        Assert.Equal(AppLanguage.AutoDetect, store.DisplayLanguage);
        Assert.Equal(AppLanguage.AutoDetect, viewModel.DisplayLanguage);
        Assert.Equal(0, viewModel.DisplayLanguageIndex);
        Assert.Equal(AppLanguage.AutoDetect, notifiedLanguage);
    }

    /// <summary>Verifies repeated ComboBox write-back for the current language does not re-enter localization refresh.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_CurrentLanguage_DoesNotReapplyLanguage()
    {
        FakeSettingsStore store = new() { DisplayLanguage = AppLanguage.German };
        int applyCount = 0;
        SettingsViewModel viewModel = new(store, _ => applyCount++, () => { });
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        bool changed = viewModel.SetDisplayLanguageIndex((int)AppLanguage.German + 1);

        Assert.False(changed);
        Assert.Equal(0, applyCount);
        Assert.DoesNotContain(nameof(SettingsViewModel.DisplayLanguageOptions), changedProperties);
    }

    /// <summary>Verifies bindable switch setters persist values and raise property change notifications.</summary>
    [Fact]
    public void TransparentProxyEnabled_Setter_PersistsAndRaisesPropertyChanged()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.TransparentProxyEnabled = false;

        Assert.False(store.TransparentProxyEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.Contains(nameof(SettingsViewModel.TransparentProxyEnabled), changedProperties);
    }

    /// <summary>Verifies transparent proxy preference can be enabled even before the mihomo service is deployed.</summary>
    [Fact]
    public void TransparentProxyEnabled_Setter_WhenMihomoServiceMissing_PersistsPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = false };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(false, false, "Not installed"));
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);

        viewModel.TransparentProxyEnabled = true;

        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.True(viewModel.CanToggleTransparentProxy);
        Assert.Equal("Not installed", viewModel.MihomoServiceStatusText);
    }

    /// <summary>Verifies deploying the mihomo service refreshes status and allows transparent proxy toggling.</summary>
    [Fact]
    public async Task DeployMihomoServiceAsync_WhenSuccessful_AllowsTransparentProxy()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = false };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(false, false, "Not installed"))
        {
            DeployResult = new MihomoServiceStatus(true, true, "Installed"),
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);

        await viewModel.DeployMihomoServiceAsync(CancellationToken.None);
        viewModel.TransparentProxyEnabled = true;

        Assert.True(service.DeployCalled);
        Assert.True(viewModel.CanToggleTransparentProxy);
        Assert.Equal("Installed", viewModel.MihomoServiceStatusText);
        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
    }

    /// <summary>Verifies uninstalling the mihomo service preserves the transparent proxy preference.</summary>
    [Fact]
    public async Task UninstallMihomoServiceAsync_PreservesTransparentProxyPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(true, true, "Installed"))
        {
            UninstallResult = new MihomoServiceStatus(false, false, "Removed"),
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);

        await viewModel.UninstallMihomoServiceAsync(CancellationToken.None);

        Assert.True(service.UninstallCalled);
        Assert.True(viewModel.CanToggleTransparentProxy);
        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.Equal("Removed", viewModel.MihomoServiceStatusText);
    }

    /// <summary>Verifies loading settings preserves a stored transparent proxy preference when the service is missing.</summary>
    [Fact]
    public void Load_WhenTransparentProxyPreferenceEnabledAndServiceMissing_PreservesPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(false, false, "Not installed"));
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);

        viewModel.Load();

        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.True(viewModel.CanToggleTransparentProxy);
    }

    /// <summary>Verifies the default mihomo service controller uses injected localization instead of global resources.</summary>
    [Fact]
    public async Task AlwaysAvailableMihomoServiceController_UsesInjectedLocalization()
    {
        AlwaysAvailableMihomoServiceController controller = new(key => key switch
        {
            "MihomoService.Status.Deployed" => "localized deployed",
            "MihomoService.Status.NotDeployed" => "localized not deployed",
            _ => key,
        });

        MihomoServiceStatus status = controller.GetStatus();
        MihomoServiceStatus uninstallStatus = await controller.UninstallAsync(CancellationToken.None);

        Assert.Equal("localized deployed", status.Message);
        Assert.Equal("localized not deployed", uninstallStatus.Message);
    }

    /// <summary>Verifies app theme selection persists and notifies the shell theme controller.</summary>
    [Fact]
    public void SetAppThemeModeIndex_ValidIndex_PersistsAndAppliesTheme()
    {
        FakeSettingsStore store = new();
        AppThemeMode? appliedTheme = null;
        SettingsViewModel viewModel = new(store, _ => { }, theme => appliedTheme = theme, () => { });

        bool changed = viewModel.SetAppThemeModeIndex((int)AppThemeMode.Dark);

        Assert.True(changed);
        Assert.Equal(AppThemeMode.Dark, store.AppThemeMode);
        Assert.Equal(AppThemeMode.Dark, viewModel.AppThemeMode);
        Assert.Equal(AppThemeMode.Dark, appliedTheme);
    }

    /// <summary>Verifies launch-at-startup switch persists and invokes the system sync callback.</summary>
    [Fact]
    public void LaunchAtStartupEnabled_Setter_PersistsAndAppliesStartupRegistration()
    {
        FakeSettingsStore store = new() { LaunchAtStartupEnabled = false };
        bool? appliedValue = null;
        SettingsViewModel viewModel = new(store, _ => { }, _ => { }, () => { }, launch => appliedValue = launch);

        viewModel.LaunchAtStartupEnabled = true;

        Assert.True(store.LaunchAtStartupEnabled);
        Assert.True(viewModel.LaunchAtStartupEnabled);
        Assert.True(appliedValue);
    }

    /// <summary>Verifies invalid language indexes are ignored.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(100)]
    public void SetDisplayLanguageIndex_InvalidIndex_DoesNotPersist(int index)
    {
        FakeSettingsStore store = new() { DisplayLanguage = AppLanguage.English };
        SettingsViewModel viewModel = new(store, _ => throw new InvalidOperationException("Should not notify."), () => { });

        bool changed = viewModel.SetDisplayLanguageIndex(index);

        Assert.False(changed);
        Assert.Equal(AppLanguage.English, store.DisplayLanguage);
    }

    /// <summary>Verifies mixed port input is rounded and persisted only inside the TCP port range.</summary>
    [Theory]
    [InlineData(double.NaN, 10000, false)]
    [InlineData(0d, 10000, false)]
    [InlineData(65536d, 10000, false)]
    [InlineData(7891.49d, 7891, true)]
    [InlineData(7891.50d, 7892, true)]
    public void SetMixedPort_ValidatesAndRoundsInput(double input, int expectedPort, bool expectedResult)
    {
        FakeSettingsStore store = new() { MixedPort = 10000 };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = viewModel.SetMixedPort(input);

        Assert.Equal(expectedResult, changed);
        Assert.Equal(expectedPort, store.MixedPort);
        Assert.Equal(expectedPort, viewModel.MixedPort);
    }

    /// <summary>Verifies bindable number-box port values reuse existing validation and rounding rules.</summary>
    [Fact]
    public void MixedPortValue_Setter_PersistsValidRoundedPort()
    {
        FakeSettingsStore store = new() { MixedPort = 10000 };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        viewModel.MixedPortValue = 7891.5d;

        Assert.Equal(7892, store.MixedPort);
        Assert.Equal(7892, viewModel.MixedPort);
        Assert.Equal(7892d, viewModel.MixedPortValue);
    }

    /// <summary>Verifies sampling changes restart the sampling service after persistence.</summary>
    [Fact]
    public void SamplingSettings_WhenPersisted_RestartSampling()
    {
        FakeSettingsStore store = new();
        int restartCount = 0;
        SettingsViewModel viewModel = new(store, _ => { }, () => restartCount++);

        viewModel.SetConnectionSamplingEnabled(false);
        bool intervalChanged = viewModel.SetConnectionSamplingIntervalSeconds(60d);

        Assert.False(store.ConnectionSamplingEnabled);
        Assert.Equal(60, store.ConnectionSamplingIntervalSeconds);
        Assert.True(intervalChanged);
        Assert.Equal(2, restartCount);
    }

    /// <summary>Verifies startup behavior selection persists only valid enum indexes.</summary>
    [Theory]
    [InlineData((int)StartupBehaviorMode.LastSetting, StartupBehaviorMode.LastSetting, true)]
    [InlineData((int)StartupBehaviorMode.StartRuleProxy, StartupBehaviorMode.StartRuleProxy, true)]
    [InlineData((int)StartupBehaviorMode.DisableProxy, StartupBehaviorMode.DisableProxy, true)]
    [InlineData(-1, StartupBehaviorMode.LastSetting, false)]
    [InlineData(100, StartupBehaviorMode.LastSetting, false)]
    public void SetStartupBehaviorModeIndex_ValidatesAndPersists(
        int index,
        StartupBehaviorMode expectedMode,
        bool expectedResult)
    {
        FakeSettingsStore store = new() { StartupBehaviorMode = StartupBehaviorMode.LastSetting };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = viewModel.SetStartupBehaviorModeIndex(index);

        Assert.Equal(expectedResult, changed);
        Assert.Equal(expectedMode, store.StartupBehaviorMode);
        Assert.Equal(expectedMode, viewModel.StartupBehaviorMode);
        Assert.Equal((int)expectedMode, viewModel.StartupBehaviorModeIndex);
    }

    /// <summary>Verifies the startup conflict check switch persists independently.</summary>
    [Fact]
    public void StartupConflictCheckEnabled_Setter_PersistsAndRaisesPropertyChanged()
    {
        FakeSettingsStore store = new() { StartupConflictCheckEnabled = true };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.StartupConflictCheckEnabled = false;

        Assert.False(store.StartupConflictCheckEnabled);
        Assert.False(viewModel.StartupConflictCheckEnabled);
        Assert.Contains(nameof(SettingsViewModel.StartupConflictCheckEnabled), changedProperties);
    }

    /// <summary>Verifies bindable option lists expose non-empty defaults without relying on ComboBoxItem binding.</summary>
    [Fact]
    public void OptionLists_ExposeNonEmptyText()
    {
        SettingsViewModel viewModel = new(new FakeSettingsStore(), _ => { }, () => { }, key => key);

        Assert.Equal(7, viewModel.DisplayLanguageOptions.Count);
        Assert.All(viewModel.DisplayLanguageOptions, Assert.NotEmpty);
        Assert.Equal(3, viewModel.AppThemeModeOptions.Count);
        Assert.All(viewModel.AppThemeModeOptions, Assert.NotEmpty);
        Assert.Equal(2, ReadProperty<IReadOnlyList<string>>(viewModel, "AppAccentColorModeOptions").Count);
        Assert.All(ReadProperty<IReadOnlyList<string>>(viewModel, "AppAccentColorModeOptions"), Assert.NotEmpty);
        Assert.Equal(3, viewModel.ProxyRecoveryModeOptions.Count);
        Assert.All(viewModel.ProxyRecoveryModeOptions, Assert.NotEmpty);
        Assert.Equal(4, viewModel.MainlandChinaFeatureModeOptions.Count);
        Assert.All(viewModel.MainlandChinaFeatureModeOptions, Assert.NotEmpty);
        Assert.Equal(3, viewModel.StartupBehaviorModeOptions.Count);
        Assert.All(viewModel.StartupBehaviorModeOptions, Assert.NotEmpty);
    }

    /// <summary>Verifies startup settings expose a dedicated section, manual conflict check text, and guide toggle state.</summary>
    [Fact]
    public void StartupSettings_ExposeSectionConflictCheckAndGuideBindings()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);

        Assert.Equal("Settings.Section.Startup", ReadProperty<string>(viewModel, "StartupSectionTitleText"));
        Assert.Equal("Settings.CheckStartupConflicts.Title", ReadProperty<string>(viewModel, "CheckStartupConflictsTitleText"));
        Assert.Equal("Settings.CheckStartupConflicts.Description", ReadProperty<string>(viewModel, "CheckStartupConflictsDescriptionText"));
        Assert.Equal("Settings.StartupGuide.Title", ReadProperty<string>(viewModel, "StartupGuideTitleText"));
        Assert.Equal("Settings.StartupGuide.Description", ReadProperty<string>(viewModel, "StartupGuideDescriptionText"));
        Assert.True(ReadProperty<bool>(viewModel, "ShowStartupGuideOnStartup"));
    }

    /// <summary>Verifies theme color settings expose system/custom options and a custom color seed.</summary>
    [Fact]
    public void AccentColorSettings_ExposeSystemAndCustomColorBindings()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);

        Assert.Equal("Settings.AppAccentColor.Title", ReadProperty<string>(viewModel, "AppAccentColorTitleText"));
        Assert.Equal("Settings.AppAccentColor.Description", ReadProperty<string>(viewModel, "AppAccentColorDescriptionText"));
        Assert.Equal("Settings.AppAccentColor.FollowSystem", ReadProperty<string>(viewModel, "AppAccentColorFollowSystemText"));
        Assert.Equal("Settings.AppAccentColor.Custom", ReadProperty<string>(viewModel, "AppAccentColorCustomText"));
        Assert.Equal(0, ReadProperty<int>(viewModel, "AppAccentColorModeIndex"));
        Assert.Equal("#FF0078D4", ReadProperty<string>(viewModel, "AppAccentColorValue"));
        Assert.False(ReadProperty<bool>(viewModel, "IsCustomAccentColorSelected"));
    }

    /// <summary>Verifies custom accent color mode and value are persisted through explicit ViewModel methods.</summary>
    [Fact]
    public void AccentColorSettings_PersistCustomModeAndColor()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);

        bool modeChanged = InvokeMethod<bool>(viewModel, "SetAppAccentColorModeIndex", 1);
        bool colorChanged = InvokeMethod<bool>(viewModel, "SetAppAccentColorValue", "#FF2D7D9A");

        Assert.True(modeChanged);
        Assert.True(colorChanged);
        Assert.Equal("Custom", store.AppAccentColorMode.ToString());
        Assert.Equal("#FF2D7D9A", store.AppAccentColorValue);
        Assert.True(ReadProperty<bool>(viewModel, "IsCustomAccentColorSelected"));
        Assert.Equal(1, ReadProperty<int>(viewModel, "AppAccentColorModeIndex"));
        Assert.Equal("#FF2D7D9A", ReadProperty<string>(viewModel, "AppAccentColorValue"));
    }

    /// <summary>Verifies custom accent color changes mark the setting as restart-required until reverted.</summary>
    [Fact]
    public void AccentColorSettings_RestartPendingTracksChangesUntilReverted()
    {
        FakeSettingsStore store = new();
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);

        InvokeMethod<bool>(viewModel, "SetAppAccentColorModeIndex", (int)AppAccentColorMode.Custom);
        InvokeMethod<bool>(viewModel, "SetAppAccentColorValue", "#FF00AA00");

        Assert.True(ReadProperty<bool>(viewModel, "IsAppAccentColorRestartPending"));
        Assert.Equal("Settings.AppAccentColor.Title*", ReadProperty<string>(viewModel, "AppAccentColorTitleText"));

        InvokeMethod<bool>(viewModel, "SetAppAccentColorModeIndex", (int)AppAccentColorMode.FollowSystem);

        Assert.False(ReadProperty<bool>(viewModel, "IsAppAccentColorRestartPending"));
        Assert.Equal("Settings.AppAccentColor.Title", ReadProperty<string>(viewModel, "AppAccentColorTitleText"));
    }

    /// <summary>Verifies restart markers compare against the currently applied app accent state.</summary>
    [Fact]
    public void AccentColorSettings_RestartPendingUsesCurrentAppliedAccentState()
    {
        FakeSettingsStore store = new()
        {
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF00AA00",
        };
        SettingsViewModel viewModel = CreateAccentRestartViewModel(
            store,
            (mode, color) => mode == AppAccentColorMode.Custom
                && !StringComparer.OrdinalIgnoreCase.Equals(color, "#FF0078D4"));

        Assert.True(ReadProperty<bool>(viewModel, "IsAppAccentColorRestartPending"));
        Assert.Equal("Settings.AppAccentColor.Title*", ReadProperty<string>(viewModel, "AppAccentColorTitleText"));

        InvokeMethod<bool>(viewModel, "SetAppAccentColorModeIndex", (int)AppAccentColorMode.FollowSystem);

        Assert.False(ReadProperty<bool>(viewModel, "IsAppAccentColorRestartPending"));
        Assert.Equal("Settings.AppAccentColor.Title", ReadProperty<string>(viewModel, "AppAccentColorTitleText"));
    }

    /// <summary>Verifies mainland China feature mode selection persists only valid enum indexes.</summary>
    [Theory]
    [InlineData((int)MainlandChinaFeatureMode.Disabled, MainlandChinaFeatureMode.Disabled, true)]
    [InlineData((int)MainlandChinaFeatureMode.AllIncludingUrlBlacklist, MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter, true)]
    [InlineData(-1, MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter, false)]
    [InlineData(100, MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter, false)]
    public void SetMainlandChinaFeatureModeIndex_ValidatesAndPersists(
        int index,
        MainlandChinaFeatureMode expectedMode,
        bool expectedResult)
    {
        FakeSettingsStore store = new()
        {
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter,
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = viewModel.SetMainlandChinaFeatureModeIndex(index);

        Assert.Equal(expectedResult, changed);
        Assert.Equal(expectedMode, store.MainlandChinaFeatureMode);
        Assert.Equal(expectedMode, viewModel.MainlandChinaFeatureMode);
        Assert.Equal((int)expectedMode, viewModel.MainlandChinaFeatureModeIndex);
    }

    /// <summary>Verifies mainland China URL blocking is persisted independently from the display mode combo box.</summary>
    [Fact]
    public void MainlandChinaUrlBlockingEnabled_Setter_PersistsSwitchOnly()
    {
        FakeSettingsStore store = new()
        {
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementOnly,
            MainlandChinaUrlBlockingEnabled = false,
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        viewModel.MainlandChinaUrlBlockingEnabled = true;

        Assert.True(store.MainlandChinaUrlBlockingEnabled);
        Assert.True(viewModel.MainlandChinaUrlBlockingEnabled);
        Assert.Equal(MainlandChinaFeatureMode.FlagReplacementOnly, store.MainlandChinaFeatureMode);
    }

    /// <summary>Verifies connection test URL input persists non-empty normalized text.</summary>
    [Fact]
    public void SetConnectionTestUrl_PersistsNonEmptyUrl()
    {
        FakeSettingsStore store = new() { ConnectionTestUrl = "https://www.google.com/generate_204" };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = viewModel.SetConnectionTestUrl(" example.com/generate_204 ");

        Assert.True(changed);
        Assert.Equal("https://example.com/generate_204", store.ConnectionTestUrl);
        Assert.Equal("https://example.com/generate_204", viewModel.ConnectionTestUrl);
    }

    /// <summary>Verifies the three registered connection-test URLs can be edited and restored to defaults.</summary>
    [Fact]
    public void ConnectionTestUrls_EditAndRestoreDefaults()
    {
        FakeSettingsStore store = new()
        {
            ConnectionTestProxyUrl1 = "https://old-one.example",
            ConnectionTestProxyUrl2 = "https://old-two.example",
            ConnectionTestDirectUrl = "https://old-direct.example",
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = InvokeMethod<bool>(
            viewModel,
            "SetConnectionTestUrls",
            ["google.com", " github.com ", "baidu.com"]);

        Assert.True(changed);
        Assert.Equal("https://google.com", store.ConnectionTestProxyUrl1);
        Assert.Equal("https://github.com", store.ConnectionTestProxyUrl2);
        Assert.Equal("https://baidu.com", store.ConnectionTestDirectUrl);
        Assert.Equal("https://google.com", ReadProperty<string>(viewModel, "ConnectionTestProxyUrl1"));

        InvokeMethod<object?>(viewModel, "ResetConnectionTestUrlsToDefaults", Array.Empty<object>());

        Assert.Equal("https://www.google.com", store.ConnectionTestProxyUrl1);
        Assert.Equal("https://github.com", store.ConnectionTestProxyUrl2);
        Assert.Equal("https://www.baidu.com", store.ConnectionTestDirectUrl);
    }

    /// <summary>Verifies data package import and export bindings expose level choices and commands.</summary>
    [Fact]
    public void DataPackageSettings_ExposeScopeAndCommandBindings()
    {
        SettingsViewModel viewModel = new(new FakeSettingsStore(), _ => { }, () => { }, key => key);

        Assert.Equal("Settings.DataPackage.Title", ReadProperty<string>(viewModel, "DataPackageTitleText"));
        Assert.Equal("Settings.DataPackage.Description", ReadProperty<string>(viewModel, "DataPackageDescriptionText"));
        Assert.Equal("Settings.DataExport.Title", ReadProperty<string>(viewModel, "DataExportTitleText"));
        Assert.Equal("Settings.DataExport.Description", ReadProperty<string>(viewModel, "DataExportDescriptionText"));
        Assert.Equal("Command.Export", ReadProperty<string>(viewModel, "ExportText"));
        Assert.Equal("Command.Import", ReadProperty<string>(viewModel, "ImportText"));
        Assert.Equal("Command.Backup", ReadProperty<string>(viewModel, "BackupText"));
    }

    /// <summary>Verifies known connection-test URLs are summarized with localized names and defaults collapse to one label.</summary>
    [Fact]
    public void ConnectionTestUrlSummary_UsesKnownLocalizedNamesAndDefaultLabel()
    {
        SettingsViewModel defaultViewModel = new(new FakeSettingsStore(), _ => { }, () => { }, key => key);

        Assert.Equal(
            "Settings.ConnectionTestUrl.Provider.Google | Settings.ConnectionTestUrl.Provider.GitHub | Settings.ConnectionTestUrl.Provider.Baidu",
            ReadProperty<string>(defaultViewModel, "ConnectionTestUrlSummaryText"));

        FakeSettingsStore store = new()
        {
            ConnectionTestProxyUrl1 = "https://www.google.com",
            ConnectionTestProxyUrl2 = "https://example.test/path",
            ConnectionTestDirectUrl = "https://www.baidu.com",
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);

        Assert.Equal(
            "Settings.ConnectionTestUrl.Provider.Google | Settings.ConnectionTestUrl.Provider.Custom | Settings.ConnectionTestUrl.Provider.Baidu",
            ReadProperty<string>(viewModel, "ConnectionTestUrlSummaryText"));
    }

    /// <summary>Verifies background sampling interval uses the user-facing 3-300 second range.</summary>
    [Theory]
    [InlineData(2d, 30, false)]
    [InlineData(3d, 3, true)]
    [InlineData(300d, 300, true)]
    [InlineData(301d, 30, false)]
    public void SetConnectionSamplingIntervalSeconds_ValidatesUserFacingRange(double input, int expectedInterval, bool expectedResult)
    {
        FakeSettingsStore store = new() { ConnectionSamplingIntervalSeconds = 30 };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = viewModel.SetConnectionSamplingIntervalSeconds(input);

        Assert.Equal(expectedResult, changed);
        Assert.Equal(expectedInterval, store.ConnectionSamplingIntervalSeconds);
        Assert.Equal(expectedInterval, viewModel.ConnectionSamplingIntervalSeconds);
    }

    /// <summary>Verifies the connection test runs through an injected probe and returns a localized success message.</summary>
    [Fact]
    public async Task RunConnectionTestAsync_ProbeSucceeds_ReturnsLocalizedStatusMessage()
    {
        List<Uri> requestedUris = [];
        SettingsViewModel viewModel = CreateConnectionTestViewModel(async (uri, _) =>
        {
            requestedUris.Add(uri);
            await Task.Yield();
            return 204;
        });

        string message = await InvokeRunConnectionTestAsync(viewModel, CancellationToken.None);

        Assert.Contains("Settings.ConnectionTestUrl.Proxy1: success 204", message);
        Assert.Contains("Settings.ConnectionTestUrl.Proxy2: success 204", message);
        Assert.Contains("Settings.ConnectionTestUrl.Direct: success 204", message);
        Assert.Equal(
            ["https://www.google.com/", "https://github.com/", "https://www.baidu.com/"],
            requestedUris.Select(uri => uri.ToString()).ToArray());
        Assert.False(ReadProperty<bool>(viewModel, "IsConnectionTestRunning"));
    }

    /// <summary>Verifies connection test failures are caught and returned as localized messages.</summary>
    [Fact]
    public async Task RunConnectionTestAsync_ProbeFails_ReturnsLocalizedFailureMessage()
    {
        SettingsViewModel viewModel = CreateConnectionTestViewModel((_, _) => throw new HttpRequestException("network unavailable"));

        string message = await InvokeRunConnectionTestAsync(viewModel, CancellationToken.None);

        Assert.Contains("Settings.ConnectionTestUrl.Proxy1: failed network unavailable", message);
        Assert.Contains("Settings.ConnectionTestUrl.Proxy2: failed network unavailable", message);
        Assert.Contains("Settings.ConnectionTestUrl.Direct: failed network unavailable", message);
        Assert.False(ReadProperty<bool>(viewModel, "IsConnectionTestRunning"));
    }

    /// <summary>Verifies reset-all settings delegates maintenance work and reloads the current settings snapshot.</summary>
    [Fact]
    public void ResetAllSettings_RunsInjectedMaintenanceAndReloadsSettings()
    {
        FakeSettingsStore store = new()
        {
            DisplayLanguage = AppLanguage.German,
            ConnectionTestUrl = "https://example.com/old",
        };
        bool resetCalled = false;
        AppLanguage? appliedLanguage = null;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            language => appliedLanguage = language,
            resetAllSettings: () =>
            {
                resetCalled = true;
                store.DisplayLanguage = AppLanguage.English;
                store.ConnectionTestUrl = "https://example.com/reset";
            },
            clearAllData: () => { });

        InvokeMethod<object?>(viewModel, "ResetAllSettings", Array.Empty<object>());

        Assert.True(resetCalled);
        Assert.Equal(AppLanguage.English, appliedLanguage);
        Assert.Equal(AppLanguage.English, viewModel.DisplayLanguage);
        Assert.Equal("https://example.com/reset", viewModel.ConnectionTestUrl);
    }

    /// <summary>Verifies clear-all data delegates maintenance work and reloads the current settings snapshot.</summary>
    [Fact]
    public void ClearAllData_RunsInjectedMaintenanceAndReloadsSettings()
    {
        FakeSettingsStore store = new()
        {
            DisplayLanguage = AppLanguage.French,
            ConnectionTestUrl = "https://example.com/old",
        };
        bool clearCalled = false;
        AppLanguage? appliedLanguage = null;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            language => appliedLanguage = language,
            resetAllSettings: () => { },
            clearAllData: () =>
            {
                clearCalled = true;
                store.DisplayLanguage = AppLanguage.AutoDetect;
                store.ConnectionTestUrl = "https://example.com/cleared";
            });

        InvokeMethod<object?>(viewModel, "ClearAllData", Array.Empty<object>());

        Assert.True(clearCalled);
        Assert.Equal(AppLanguage.AutoDetect, appliedLanguage);
        Assert.Equal(AppLanguage.AutoDetect, viewModel.DisplayLanguage);
        Assert.Equal("https://example.com/cleared", viewModel.ConnectionTestUrl);
    }

    /// <summary>Verifies settings can be reset by visible settings group without touching unrelated groups.</summary>
    [Fact]
    public void ResetGroupDefaults_RestoresExpectedSettingsPerGroup()
    {
        FakeSettingsStore store = new()
        {
            DisplayLanguage = AppLanguage.French,
            AppThemeMode = AppThemeMode.Dark,
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF00AA00",
            LaunchAtStartupEnabled = true,
            TransparentProxyEnabled = false,
            MixedPort = 12345,
            ConnectionSamplingEnabled = false,
            ConnectionSamplingIntervalSeconds = 90,
            StartupConflictCheckEnabled = false,
            StartupBehaviorMode = StartupBehaviorMode.DisableProxy,
            ShowStartupGuideOnStartup = false,
            CheckStaleProxyOnStartup = false,
            RestoreProxyOnExit = false,
            ProxyRecoveryMode = ProxyRecoveryMode.EnableProxy,
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled,
            MainlandChinaUrlBlockingEnabled = true,
            ConnectionTestUrl = "https://example.com/test",
        };
        AppLanguage? appliedLanguage = null;
        AppThemeMode? appliedTheme = null;
        bool? appliedLaunch = null;
        int samplingRestarts = 0;
        SettingsViewModel viewModel = new(
            store,
            language => appliedLanguage = language,
            theme => appliedTheme = theme,
            () => samplingRestarts++,
            isEnabled => appliedLaunch = isEnabled);

        InvokeMethod<object?>(viewModel, "ResetBasicSettingsToDefaults", Array.Empty<object>());

        Assert.Equal(AppLanguage.AutoDetect, store.DisplayLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, store.AppThemeMode);
        Assert.Equal(AppAccentColorMode.FollowSystem, store.AppAccentColorMode);
        Assert.Equal("#FF0078D4", store.AppAccentColorValue);
        Assert.Equal(AppLanguage.AutoDetect, appliedLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, appliedTheme);

        InvokeMethod<object?>(viewModel, "ResetStartupSettingsToDefaults", Array.Empty<object>());

        Assert.False(store.LaunchAtStartupEnabled);
        Assert.True(store.StartupConflictCheckEnabled);
        Assert.True(store.ShowStartupGuideOnStartup);
        Assert.Equal(StartupBehaviorMode.LastSetting, store.StartupBehaviorMode);
        Assert.False(appliedLaunch);

        InvokeMethod<object?>(viewModel, "ResetProxySettingsToDefaults", Array.Empty<object>());

        Assert.True(store.TransparentProxyEnabled);
        Assert.Equal(10000, store.MixedPort);
        Assert.True(store.ConnectionSamplingEnabled);
        Assert.Equal(30, store.ConnectionSamplingIntervalSeconds);
        Assert.Equal("https://www.google.com/generate_204", store.ConnectionTestUrl);
        Assert.Equal(1, samplingRestarts);

        InvokeMethod<object?>(viewModel, "ResetWindowsNativeSettingsToDefaults", Array.Empty<object>());

        Assert.True(store.CheckStaleProxyOnStartup);
        Assert.True(store.RestoreProxyOnExit);
        Assert.Equal(ProxyRecoveryMode.DisableProxy, store.ProxyRecoveryMode);

        InvokeMethod<object?>(viewModel, "ResetMainlandChinaSettingsToDefaults", Array.Empty<object>());

        Assert.Equal(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion, store.MainlandChinaFeatureMode);
        Assert.False(store.MainlandChinaUrlBlockingEnabled);
    }

    /// <summary>Verifies startup conflict checks are delegated through an injected checker using the current mixed port.</summary>
    [Fact]
    public void CheckStartupConflicts_UsesInjectedCheckerAndMixedPort()
    {
        FakeSettingsStore store = new() { MixedPort = 12345 };
        int? checkedPort = null;
        StartupConflictIssue expectedIssue = new(
            StartupConflictKind.MixedPortOccupied,
            "Port occupied",
            "Port 12345 is occupied.",
            "Inspect",
            _ => Task.FromResult(new StartupConflictRepairResult(false, "No repair")));
        SettingsViewModel viewModel = CreateStartupConflictViewModel(store, port =>
        {
            checkedPort = port;
            return [expectedIssue];
        });

        IReadOnlyList<StartupConflictIssue> issues = InvokeMethod<IReadOnlyList<StartupConflictIssue>>(
            viewModel,
            "CheckStartupConflicts",
            Array.Empty<object>());

        Assert.Equal(12345, checkedPort);
        StartupConflictIssue issue = Assert.Single(issues);
        Assert.Same(expectedIssue, issue);
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public AppLanguage DisplayLanguage { get; set; } = AppLanguage.AutoDetect;

        public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.FollowSystem;

        public AppAccentColorMode AppAccentColorMode { get; set; } = AppAccentColorMode.FollowSystem;

        public string AppAccentColorValue { get; set; } = "#FF0078D4";

        public bool LaunchAtStartupEnabled { get; set; }

        public bool TransparentProxyEnabled { get; set; } = true;

        public int MixedPort { get; set; } = 10000;

        public bool ConnectionSamplingEnabled { get; set; } = true;

        public int ConnectionSamplingIntervalSeconds { get; set; } = 30;

        public bool StartupConflictCheckEnabled { get; set; } = true;

        public StartupBehaviorMode StartupBehaviorMode { get; set; } = StartupBehaviorMode.LastSetting;

        public bool ShowStartupGuideOnStartup { get; set; } = true;

        public bool CheckStaleProxyOnStartup { get; set; } = true;

        public bool RestoreProxyOnExit { get; set; } = true;

        public ProxyRecoveryMode ProxyRecoveryMode { get; set; } = ProxyRecoveryMode.DisableProxy;

        public MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; } = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;

        public bool MainlandChinaUrlBlockingEnabled { get; set; }

        public string ConnectionTestUrl { get; set; } = "https://www.google.com/generate_204";

        public string ConnectionTestProxyUrl1 { get; set; } = "https://www.google.com";

        public string ConnectionTestProxyUrl2 { get; set; } = "https://github.com";

        public string ConnectionTestDirectUrl { get; set; } = "https://www.baidu.com";
    }

    private static SettingsViewModel CreateConnectionTestViewModel(Func<Uri, CancellationToken, Task<int>> testConnectionAsync)
    {
        ConstructorInfo? constructor = typeof(SettingsViewModel).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(ISettingsStore),
                typeof(Action<AppLanguage>),
                typeof(Action<AppThemeMode>),
                typeof(Action),
                typeof(Action<bool>),
                typeof(Func<string, string>),
                typeof(Func<SettingsProxyInformation>),
                typeof(SettingsDiagnosticsViewModel),
                typeof(IMihomoServiceController),
                typeof(Action<AppAccentColorMode, string>),
                typeof(Func<Uri, CancellationToken, Task<int>>),
                typeof(Action),
                typeof(Action),
                typeof(Func<int, IReadOnlyList<StartupConflictIssue>>),
                typeof(Func<AppAccentColorMode, string, bool>),
            ],
            modifiers: null);
        Assert.NotNull(constructor);

        return Assert.IsType<SettingsViewModel>(constructor.Invoke(
        [
            new FakeSettingsStore(),
            (Action<AppLanguage>)(_ => { }),
            (Action<AppThemeMode>)(_ => { }),
            () => { },
            (Action<bool>)(_ => { }),
            (Func<string, string>)(key => key switch
            {
                "Settings.ConnectionTest.Succeeded.Format" => "success {0}",
                "Settings.ConnectionTest.Failed.Format" => "failed {0}",
                _ => key,
            }),
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            null,
            null,
            null,
            testConnectionAsync,
            (Action)(() => { }),
            (Action)(() => { }),
            (Func<int, IReadOnlyList<StartupConflictIssue>>)(_ => []),
            null,
        ]));
    }

    private static SettingsViewModel CreateMaintenanceViewModel(
        FakeSettingsStore store,
        Action<AppLanguage> applyLanguage,
        Action resetAllSettings,
        Action clearAllData)
    {
        ConstructorInfo? constructor = typeof(SettingsViewModel).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(ISettingsStore),
                typeof(Action<AppLanguage>),
                typeof(Action<AppThemeMode>),
                typeof(Action),
                typeof(Action<bool>),
                typeof(Func<string, string>),
                typeof(Func<SettingsProxyInformation>),
                typeof(SettingsDiagnosticsViewModel),
                typeof(IMihomoServiceController),
                typeof(Action<AppAccentColorMode, string>),
                typeof(Func<Uri, CancellationToken, Task<int>>),
                typeof(Action),
                typeof(Action),
                typeof(Func<int, IReadOnlyList<StartupConflictIssue>>),
                typeof(Func<AppAccentColorMode, string, bool>),
            ],
            modifiers: null);
        Assert.NotNull(constructor);

        return Assert.IsType<SettingsViewModel>(constructor.Invoke(
        [
            store,
            applyLanguage,
            (Action<AppThemeMode>)(_ => { }),
            () => { },
            (Action<bool>)(_ => { }),
            (Func<string, string>)(key => key),
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            null,
            null,
            null,
            (Func<Uri, CancellationToken, Task<int>>)((_, _) => Task.FromResult(204)),
            resetAllSettings,
            clearAllData,
            (Func<int, IReadOnlyList<StartupConflictIssue>>)(_ => []),
            null,
        ]));
    }

    private static SettingsViewModel CreateStartupConflictViewModel(
        FakeSettingsStore store,
        Func<int, IReadOnlyList<StartupConflictIssue>> checkStartupConflicts)
    {
        ConstructorInfo? constructor = typeof(SettingsViewModel).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(ISettingsStore),
                typeof(Action<AppLanguage>),
                typeof(Action<AppThemeMode>),
                typeof(Action),
                typeof(Action<bool>),
                typeof(Func<string, string>),
                typeof(Func<SettingsProxyInformation>),
                typeof(SettingsDiagnosticsViewModel),
                typeof(IMihomoServiceController),
                typeof(Action<AppAccentColorMode, string>),
                typeof(Func<Uri, CancellationToken, Task<int>>),
                typeof(Action),
                typeof(Action),
                typeof(Func<int, IReadOnlyList<StartupConflictIssue>>),
                typeof(Func<AppAccentColorMode, string, bool>),
            ],
            modifiers: null);
        Assert.NotNull(constructor);

        return Assert.IsType<SettingsViewModel>(constructor.Invoke(
        [
            store,
            (Action<AppLanguage>)(_ => { }),
            (Action<AppThemeMode>)(_ => { }),
            () => { },
            (Action<bool>)(_ => { }),
            (Func<string, string>)(key => key),
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            null,
            null,
            null,
            (Func<Uri, CancellationToken, Task<int>>)((_, _) => Task.FromResult(204)),
            (Action)(() => { }),
            (Action)(() => { }),
            checkStartupConflicts,
            null,
        ]));
    }

    private static SettingsViewModel CreateAccentRestartViewModel(
        FakeSettingsStore store,
        Func<AppAccentColorMode, string, bool> isAccentColorRestartPending)
    {
        ConstructorInfo? constructor = typeof(SettingsViewModel).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            binder: null,
            types:
            [
                typeof(ISettingsStore),
                typeof(Action<AppLanguage>),
                typeof(Action<AppThemeMode>),
                typeof(Action),
                typeof(Action<bool>),
                typeof(Func<string, string>),
                typeof(Func<SettingsProxyInformation>),
                typeof(SettingsDiagnosticsViewModel),
                typeof(IMihomoServiceController),
                typeof(Action<AppAccentColorMode, string>),
                typeof(Func<Uri, CancellationToken, Task<int>>),
                typeof(Action),
                typeof(Action),
                typeof(Func<int, IReadOnlyList<StartupConflictIssue>>),
                typeof(Func<AppAccentColorMode, string, bool>),
            ],
            modifiers: null);
        Assert.NotNull(constructor);

        return Assert.IsType<SettingsViewModel>(constructor.Invoke(
        [
            store,
            (Action<AppLanguage>)(_ => { }),
            (Action<AppThemeMode>)(_ => { }),
            () => { },
            (Action<bool>)(_ => { }),
            (Func<string, string>)(key => key),
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            null,
            null,
            null,
            (Func<Uri, CancellationToken, Task<int>>)((_, _) => Task.FromResult(204)),
            (Action)(() => { }),
            (Action)(() => { }),
            (Func<int, IReadOnlyList<StartupConflictIssue>>)(_ => []),
            isAccentColorRestartPending,
        ]));
    }

    /// <summary>Reads a view model property by name so the red test can specify a new binding contract before implementation.</summary>
    /// <param name="viewModel">Settings view model under test.</param>
    /// <param name="propertyName">Property name to read.</param>
    /// <typeparam name="T">Expected property value type.</typeparam>
    /// <returns>The typed property value.</returns>
    private static T ReadProperty<T>(SettingsViewModel viewModel, string propertyName)
    {
        System.Reflection.PropertyInfo? property = typeof(SettingsViewModel).GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property.GetValue(viewModel));
    }

    /// <summary>Invokes a view model method by name so red tests can describe new methods before implementation.</summary>
    /// <param name="viewModel">Settings view model under test.</param>
    /// <param name="methodName">Method name to invoke.</param>
    /// <param name="argument">Single method argument.</param>
    /// <typeparam name="T">Expected return value type.</typeparam>
    /// <returns>The typed method result.</returns>
    private static T InvokeMethod<T>(SettingsViewModel viewModel, string methodName, object argument)
    {
        System.Reflection.MethodInfo? method = typeof(SettingsViewModel).GetMethod(methodName);
        Assert.NotNull(method);
        return Assert.IsType<T>(method.Invoke(viewModel, [argument]));
    }

    private static T InvokeMethod<T>(SettingsViewModel viewModel, string methodName, object[] arguments)
    {
        System.Reflection.MethodInfo? method = typeof(SettingsViewModel).GetMethod(methodName);
        Assert.NotNull(method);
        object? result = method.Invoke(viewModel, arguments);
        return result is null && default(T) is null ? default! : Assert.IsAssignableFrom<T>(result);
    }

    private static async Task<string> InvokeRunConnectionTestAsync(SettingsViewModel viewModel, CancellationToken cancellationToken)
    {
        MethodInfo? method = typeof(SettingsViewModel).GetMethod("RunConnectionTestAsync");
        Assert.NotNull(method);
        object? value = method.Invoke(viewModel, [cancellationToken]);
        Task<string> task = Assert.IsAssignableFrom<Task<string>>(value);
        return await task;
    }

    /// <summary>Fake mihomo service controller for transparent proxy settings tests.</summary>
    private sealed class FakeMihomoServiceController : IMihomoServiceController
    {
        /// <summary>Initializes a fake controller with a starting status.</summary>
        /// <param name="status">Initial service status.</param>
        public FakeMihomoServiceController(MihomoServiceStatus status)
        {
            CurrentStatus = status;
            DeployResult = status;
            UninstallResult = status;
        }

        /// <summary>Gets or sets current service status.</summary>
        /// <value>Current fake status.</value>
        public MihomoServiceStatus CurrentStatus { get; set; }

        /// <summary>Gets or sets deploy result.</summary>
        /// <value>Result returned by deploy.</value>
        public MihomoServiceStatus DeployResult { get; set; }

        /// <summary>Gets or sets uninstall result.</summary>
        /// <value>Result returned by uninstall.</value>
        public MihomoServiceStatus UninstallResult { get; set; }

        /// <summary>Gets whether deploy was called.</summary>
        /// <value>True when deploy was called.</value>
        public bool DeployCalled { get; private set; }

        /// <summary>Gets whether uninstall was called.</summary>
        /// <value>True when uninstall was called.</value>
        public bool UninstallCalled { get; private set; }

        /// <summary>Gets current fake service status.</summary>
        /// <returns>Current fake status.</returns>
        public MihomoServiceStatus GetStatus()
        {
            return CurrentStatus;
        }

        /// <summary>Deploys the fake service.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured deploy result.</returns>
        public Task<MihomoServiceStatus> DeployAsync(CancellationToken cancellationToken)
        {
            DeployCalled = true;
            CurrentStatus = DeployResult;
            return Task.FromResult(CurrentStatus);
        }

        /// <summary>Uninstalls the fake service.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured uninstall result.</returns>
        public Task<MihomoServiceStatus> UninstallAsync(CancellationToken cancellationToken)
        {
            UninstallCalled = true;
            CurrentStatus = UninstallResult;
            return Task.FromResult(CurrentStatus);
        }
    }
}
