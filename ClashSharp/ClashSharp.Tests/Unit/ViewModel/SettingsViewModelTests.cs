/*
 * Settings ViewModel Tests
 * Verifies the settings view model owns settings state transitions without WinUI controls
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/SettingsViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.ViewModel;

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
        };

        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        viewModel.Load();

        Assert.Equal(AppLanguage.French, viewModel.DisplayLanguage);
        Assert.Equal((int)AppLanguage.French + 1, viewModel.DisplayLanguageIndex);
        Assert.Equal(AppThemeMode.Dark, viewModel.AppThemeMode);
        Assert.Equal((int)AppThemeMode.Dark, viewModel.AppThemeModeIndex);
        Assert.True(viewModel.LaunchAtStartupEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
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

    /// <summary>Verifies transparent proxy cannot be enabled until the mihomo service is deployed.</summary>
    [Fact]
    public void TransparentProxyEnabled_Setter_WhenMihomoServiceMissing_DoesNotEnable()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = false };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(false, false, "Not installed"));
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);

        viewModel.TransparentProxyEnabled = true;

        Assert.False(store.TransparentProxyEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.False(viewModel.CanToggleTransparentProxy);
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

    /// <summary>Verifies uninstalling the mihomo service disables the transparent proxy preference.</summary>
    [Fact]
    public async Task UninstallMihomoServiceAsync_DisablesTransparentProxyPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(true, true, "Installed"))
        {
            UninstallResult = new MihomoServiceStatus(false, false, "Removed"),
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);

        await viewModel.UninstallMihomoServiceAsync(CancellationToken.None);

        Assert.True(service.UninstallCalled);
        Assert.False(viewModel.CanToggleTransparentProxy);
        Assert.False(store.TransparentProxyEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.Equal("Removed", viewModel.MihomoServiceStatusText);
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
        Assert.Equal(3, viewModel.ProxyRecoveryModeOptions.Count);
        Assert.All(viewModel.ProxyRecoveryModeOptions, Assert.NotEmpty);
        Assert.Equal(4, viewModel.MainlandChinaFeatureModeOptions.Count);
        Assert.All(viewModel.MainlandChinaFeatureModeOptions, Assert.NotEmpty);
        Assert.Equal(3, viewModel.StartupBehaviorModeOptions.Count);
        Assert.All(viewModel.StartupBehaviorModeOptions, Assert.NotEmpty);
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

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public AppLanguage DisplayLanguage { get; set; } = AppLanguage.AutoDetect;

        public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.FollowSystem;

        public bool LaunchAtStartupEnabled { get; set; }

        public bool TransparentProxyEnabled { get; set; } = true;

        public int MixedPort { get; set; } = 10000;

        public bool ConnectionSamplingEnabled { get; set; } = true;

        public int ConnectionSamplingIntervalSeconds { get; set; } = 30;

        public bool StartupConflictCheckEnabled { get; set; } = true;

        public StartupBehaviorMode StartupBehaviorMode { get; set; } = StartupBehaviorMode.LastSetting;

        public bool CheckStaleProxyOnStartup { get; set; } = true;

        public bool RestoreProxyOnExit { get; set; } = true;

        public ProxyRecoveryMode ProxyRecoveryMode { get; set; } = ProxyRecoveryMode.DisableProxy;

        public MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; } = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;

        public bool MainlandChinaUrlBlockingEnabled { get; set; }

        public string ConnectionTestUrl { get; set; } = "https://www.google.com/generate_204";
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
