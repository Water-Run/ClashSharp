using System.Collections.Specialized;
using System.Net.Http;
using System.Reflection;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;
using ClashSharp.Service;
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
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF2D7D9A",
            LaunchAtStartupEnabled = true,
            TransparentProxyEnabled = false,
            MixedPort = 10990,
            ConnectionSamplingEnabled = false,
            ConnectionSamplingIntervalSeconds = 45,
            StartupConflictCheckEnabled = false,
            StartupBehaviorMode = StartupBehaviorMode.DisableProxy,
            CloseBehaviorMode = CloseBehaviorMode.ConfirmExit,
            TriggersEnabled = false,
            TriggerNotificationsEnabled = false,
            TrayUseMonochromeInactiveIcon = false,
            TrayVisibleFeatureIds = "status,settings",
            CheckStaleProxyOnStartup = false,
            RestoreProxyOnExit = false,
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
        Assert.Equal(CloseBehaviorMode.ConfirmExit, viewModel.CloseBehaviorMode);
        Assert.Equal((int)CloseBehaviorMode.ConfirmExit, viewModel.CloseBehaviorModeIndex);
        Assert.False(viewModel.TriggersEnabled);
        Assert.False(viewModel.TriggerNotificationsEnabled);
        Assert.False(viewModel.TrayUseMonochromeInactiveIcon);
        Assert.Equal("status,settings", viewModel.TrayVisibleFeatureIds);
        Assert.False(viewModel.CheckStaleProxyOnStartup);
        Assert.False(viewModel.RestoreProxyOnExit);
        Assert.Equal(MainlandChinaFeatureMode.AllIncludingUrlBlacklist, viewModel.MainlandChinaFeatureMode);
        Assert.Equal((int)MainlandChinaFeatureMode.AllIncludingUrlBlacklist, viewModel.MainlandChinaFeatureModeIndex);
        Assert.True(viewModel.MainlandChinaUrlBlockingEnabled);
        Assert.Equal("https://example.com/generate_204", viewModel.ConnectionTestUrl);
        Assert.Equal("https://proxy-one.example", ReadProperty<string>(viewModel, "ConnectionTestProxyUrl1"));
        Assert.Equal("https://proxy-two.example", ReadProperty<string>(viewModel, "ConnectionTestProxyUrl2"));
        Assert.Equal("https://direct.example", ReadProperty<string>(viewModel, "ConnectionTestDirectUrl"));
    }

    /// <summary>Verifies language selection persists and is marked for restart instead of applying immediately.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_ValidIndex_PersistsAndMarksRestartPending()
    {
        FakeSettingsStore store = new();
        int applyCount = 0;
        AppLanguage? notifiedLanguage = null;
        SettingsViewModel viewModel = new(store, language =>
        {
            applyCount++;
            notifiedLanguage = language;
        }, () => { });

        bool changed = viewModel.SetDisplayLanguageIndex((int)AppLanguage.German + 1);

        Assert.True(changed);
        Assert.Equal(AppLanguage.German, store.DisplayLanguage);
        Assert.Equal(AppLanguage.German, viewModel.DisplayLanguage);
        Assert.Equal(0, applyCount);
        Assert.Null(notifiedLanguage);
        Assert.True(viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Verifies the first language option stores automatic detection for next restart.</summary>
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
        Assert.Null(notifiedLanguage);
        Assert.True(viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Verifies language switching no longer relocalizes option sources during the current process.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_DoesNotRelocalizeOptionsBeforeRestart()
    {
        FakeSettingsStore store = new() { DisplayLanguage = AppLanguage.English };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);
        IReadOnlyList<string> originalOptions = viewModel.DisplayLanguageOptions;

        bool changed = viewModel.SetDisplayLanguageIndex(0);

        Assert.True(changed);
        Assert.Same(originalOptions, viewModel.DisplayLanguageOptions);
        Assert.Equal(0, viewModel.DisplayLanguageIndex);
        Assert.Equal(7, viewModel.DisplayLanguageOptions.Count);
        Assert.All(viewModel.DisplayLanguageOptions, Assert.NotEmpty);

        changed = viewModel.SetDisplayLanguageIndex((int)AppLanguage.German + 1);

        Assert.True(changed);
        Assert.Same(originalOptions, viewModel.DisplayLanguageOptions);
        Assert.Equal((int)AppLanguage.German + 1, viewModel.DisplayLanguageIndex);
        Assert.Equal(7, viewModel.DisplayLanguageOptions.Count);
        Assert.All(viewModel.DisplayLanguageOptions, Assert.NotEmpty);
    }

    /// <summary>Verifies language switching only updates the selected index and restart state.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_DoesNotMutateLanguageOptionCollection()
    {
        FakeSettingsStore store = new() { DisplayLanguage = AppLanguage.English };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);
        INotifyCollectionChanged collection = Assert.IsAssignableFrom<INotifyCollectionChanged>(viewModel.DisplayLanguageOptions);
        List<int> countsDuringRefresh = [];
        List<NotifyCollectionChangedAction> collectionActions = [];
        List<string?> changedProperties = [];
        collection.CollectionChanged += (_, args) =>
        {
            countsDuringRefresh.Add(viewModel.DisplayLanguageOptions.Count);
            collectionActions.Add(args.Action);
        };
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        bool changed = viewModel.SetDisplayLanguageIndex(0);

        Assert.True(changed);
        Assert.Empty(countsDuringRefresh);
        Assert.Empty(collectionActions);
        Assert.DoesNotContain(nameof(SettingsViewModel.DisplayLanguageOptions), changedProperties);
        Assert.Equal(0, viewModel.DisplayLanguageIndex);
        Assert.All(viewModel.DisplayLanguageOptions, Assert.NotEmpty);
    }

    /// <summary>Verifies every localized ComboBox option source remains stable and populated after selecting a restart-required language.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_KeepsSelectorOptionsStableUntilRestart()
    {
        FakeSettingsStore store = new() { DisplayLanguage = AppLanguage.English };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);
        IReadOnlyList<string> languageOptions = viewModel.DisplayLanguageOptions;
        IReadOnlyList<string> themeOptions = viewModel.AppThemeModeOptions;
        IReadOnlyList<string> accentOptions = viewModel.AppAccentColorModeOptions;
        IReadOnlyList<string> startupOptions = viewModel.StartupBehaviorModeOptions;
        IReadOnlyList<string> closeOptions = viewModel.CloseBehaviorModeOptions;
        IReadOnlyList<string> mainlandOptions = viewModel.MainlandChinaFeatureModeOptions;
        IReadOnlyList<string> notificationOptions = viewModel.NotificationLevelOptions;

        bool changed = viewModel.SetDisplayLanguageIndex(0);

        Assert.True(changed);
        Assert.Same(languageOptions, viewModel.DisplayLanguageOptions);
        Assert.Same(themeOptions, viewModel.AppThemeModeOptions);
        Assert.Same(accentOptions, viewModel.AppAccentColorModeOptions);
        Assert.Same(startupOptions, viewModel.StartupBehaviorModeOptions);
        Assert.Same(closeOptions, viewModel.CloseBehaviorModeOptions);
        Assert.Same(mainlandOptions, viewModel.MainlandChinaFeatureModeOptions);
        Assert.Same(notificationOptions, viewModel.NotificationLevelOptions);
        Assert.All(
            [
                viewModel.DisplayLanguageOptions,
                viewModel.AppThemeModeOptions,
                viewModel.AppAccentColorModeOptions,
                viewModel.StartupBehaviorModeOptions,
                viewModel.CloseBehaviorModeOptions,
                viewModel.MainlandChinaFeatureModeOptions,
                viewModel.NotificationLevelOptions,
            ],
            options =>
            {
                Assert.NotEmpty(options);
                Assert.All(options, Assert.NotEmpty);
            });
    }

    /// <summary>Verifies repeated ComboBox write-back for the current language does not re-enter localization refresh.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_CurrentLanguage_DoesNotReapplyLanguage()
    {
        FakeSettingsStore store = new() { DisplayLanguage = AppLanguage.German };
        int applyCount = 0;
        SettingsViewModel viewModel = new(store, _ => applyCount++, () => { });
        viewModel.Load();
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
        viewModel.Load();
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.TransparentProxyEnabled = false;

        Assert.False(store.TransparentProxyEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.Contains(nameof(SettingsViewModel.TransparentProxyEnabled), changedProperties);
    }

    /// <summary>Verifies transparent proxy cannot be enabled before the mihomo service is deployed.</summary>
    [Fact]
    public void TransparentProxyEnabled_Setter_WhenMihomoServiceMissing_RejectsRequestedPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = false };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(false, false, "Not installed"));
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);
        viewModel.Load();

        viewModel.TransparentProxyEnabled = true;

        Assert.False(store.TransparentProxyEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.False(viewModel.CanToggleTransparentProxy);
        Assert.Equal("Not installed", viewModel.MihomoServiceStatusText);
    }

    /// <summary>Verifies loading settings disables a stale transparent proxy preference when the service is missing.</summary>
    [Fact]
    public void Load_WhenTransparentProxyPreferenceEnabledAndServiceMissing_PreservesRequestedPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        FakeMihomoServiceController service = new(new MihomoServiceStatus(false, false, "Not installed"));
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);

        viewModel.Load();

        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.False(viewModel.CanToggleTransparentProxy);
    }

    /// <summary>Verifies an uninitialized service cache cannot erase the user's transparent proxy preference.</summary>
    [Fact]
    public void Load_WhenTransparentProxyPreferenceEnabledAndServiceStatusUnknown_PreservesPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        FakeMihomoServiceController service = new(MihomoServiceStatus.Unknown("Not checked"));

        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);
        viewModel.Load();

        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.False(viewModel.CanToggleTransparentProxy);
        Assert.Equal("Not checked", viewModel.MihomoServiceStatusText);
    }

    [Fact]
    public void Load_WhenUnconfirmedStatusCarriesStaleInstalledBits_DisablesToggleAndPreservesPreference()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        MihomoServiceStatus unconfirmed = new(true, true, "Not checked")
        {
            ObservationState = MihomoServiceObservationState.Unknown,
        };
        FakeMihomoServiceController service = new(unconfirmed);

        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);
        viewModel.Load();

        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.False(viewModel.CanToggleTransparentProxy);
    }

    /// <summary>Verifies an installed service cannot enable TUN without a valid Installer association.</summary>
    [Fact]
    public void Load_WhenInstalledServiceHasProvisioningFailure_DisablesTransparentProxyToggle()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        MihomoServiceStatus unprovisioned = new(true, false, "Association invalid")
        {
            ProvisioningFailureCode = "service.provisioning.association_invalid",
        };
        FakeMihomoServiceController service = new(unprovisioned);

        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);
        viewModel.Load();

        Assert.True(store.TransparentProxyEnabled);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.False(viewModel.CanToggleTransparentProxy);
        Assert.Contains(
            "[service.provisioning.association_invalid]",
            viewModel.MihomoServiceDiagnosticText,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Load_WhenServiceStatusIsDefault_UsesUnknownStatusText()
    {
        FakeSettingsStore store = new() { TransparentProxyEnabled = true };
        FakeMihomoServiceController service = new(default);

        SettingsViewModel viewModel = new(store, _ => { }, () => { }, service);
        viewModel.Load();

        Assert.Equal("MihomoService.Status.Unknown", viewModel.MihomoServiceStatusText);
        Assert.True(store.TransparentProxyEnabled);
        Assert.False(viewModel.CanToggleTransparentProxy);
    }

    /// <summary>Verifies the default mihomo service controller uses injected localization instead of global resources.</summary>
    [Fact]
    public void AlwaysAvailableMihomoServiceController_UsesInjectedLocalization()
    {
        AlwaysAvailableMihomoServiceController controller = new(key => key switch
        {
            "MihomoService.Status.Deployed" => "localized deployed",
            _ => key,
        });

        MihomoServiceStatus status = controller.GetLatestStatus();

        Assert.Equal("localized deployed", status.Message);
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

    /// <summary>Verifies failed startup registration restores the last externally applied state and reports the error.</summary>
    [Fact]
    public async Task LaunchAtStartupEnabled_WhenRegistrationFails_RestoresAppliedState()
    {
        FakeSettingsStore store = new() { LaunchAtStartupEnabled = false };
        FakeApplicationErrorSink errorSink = new();
        InvalidOperationException failure = new("startup registration failed");
        SettingsViewModel viewModel = CreateRuntimeMutationViewModel(
            store,
            applyLaunchAtStartupAsync: async (_, _) =>
            {
                await Task.Yield();
                throw failure;
            },
            errorSink: errorSink);

        viewModel.LaunchAtStartupEnabled = true;
        await Assert.IsAssignableFrom<Task>(viewModel.ApplyLaunchAtStartupCommand.ExecutionTask);

        Assert.False(store.LaunchAtStartupEnabled);
        Assert.False(viewModel.LaunchAtStartupEnabled);
        Assert.True(viewModel.HasOperationError);
        Assert.Equal("Application.UnexpectedError", viewModel.OperationErrorText);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("settings-launch-at-startup", error.OperationName);
        Assert.Same(failure, error.Exception);
    }

    /// <summary>Verifies cancellation restores applied state without presenting cancellation as a runtime failure.</summary>
    [Fact]
    public async Task ApplyLaunchAtStartupCommand_WhenCancelled_RestoresStateWithoutOperationError()
    {
        FakeSettingsStore store = new() { LaunchAtStartupEnabled = false };
        SettingsViewModel viewModel = CreateRuntimeMutationViewModel(
            store,
            applyLaunchAtStartupAsync: (_, token) => Task.FromCanceled(token));
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            viewModel.ApplyLaunchAtStartupCommand.ExecuteAsync(null, cancellation.Token));

        Assert.False(store.LaunchAtStartupEnabled);
        Assert.False(viewModel.LaunchAtStartupEnabled);
        Assert.False(viewModel.HasOperationError);
        Assert.Equal(string.Empty, viewModel.OperationErrorText);
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
        viewModel.Load();

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

    [Fact]
    public async Task NetworkSettings_WhenRuntimeTransactionSucceeds_CommitsLatestRequest()
    {
        FakeSettingsStore store = new()
        {
            TransparentProxyEnabled = false,
            MixedPort = 10000,
        };
        List<(bool TunEnabled, int MixedPort)> requests = [];
        SettingsViewModel viewModel = CreateRuntimeMutationViewModel(
            store,
            applyNetworkSettingsAsync: (tunEnabled, mixedPort, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                requests.Add((tunEnabled, mixedPort));
                store.TransparentProxyEnabled = tunEnabled;
                store.MixedPort = mixedPort;
                return Task.CompletedTask;
            });

        viewModel.TransparentProxyEnabled = true;
        await viewModel.ApplyNetworkSettingsCommand.ExecutionTask!;
        viewModel.MixedPortValue = 12345;
        await viewModel.ApplyNetworkSettingsCommand.ExecutionTask!;

        Assert.Equal([(true, 10000), (true, 12345)], requests);
        Assert.True(store.TransparentProxyEnabled);
        Assert.Equal(12345, store.MixedPort);
    }

    [Fact]
    public async Task NetworkSettings_WhenRuntimeTransactionFails_RestoresAppliedRequest()
    {
        FakeSettingsStore store = new()
        {
            TransparentProxyEnabled = false,
            MixedPort = 10000,
        };
        FakeApplicationErrorSink errorSink = new();
        SettingsViewModel viewModel = CreateRuntimeMutationViewModel(
            store,
            errorSink: errorSink,
            applyNetworkSettingsAsync: (_, _, _) =>
                Task.FromException(new InvalidOperationException("runtime rejected")));

        viewModel.TransparentProxyEnabled = true;
        await viewModel.ApplyNetworkSettingsCommand.ExecutionTask!;

        Assert.False(store.TransparentProxyEnabled);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.Equal(10000, viewModel.MixedPort);
        Assert.True(viewModel.HasOperationError);
        Assert.Single(errorSink.Errors);
    }

    [Fact]
    public async Task NetworkSettings_WhenRuntimeTransactionHasStableCode_ShowsActionableDiagnostic()
    {
        FakeSettingsStore store = new()
        {
            TransparentProxyEnabled = false,
            MixedPort = 10000,
        };
        FakeApplicationErrorSink errorSink = new();
        StableRuntimeDiagnosticException failure = new(
            "geo.assets_missing",
            "runtime rejected");
        SettingsViewModel viewModel = CreateRuntimeMutationViewModel(
            store,
            errorSink: errorSink,
            applyNetworkSettingsAsync: (_, _, _) => Task.FromException(failure));

        viewModel.MixedPortValue = 12000;
        await viewModel.ApplyNetworkSettingsCommand.ExecutionTask!;

        Assert.False(store.TransparentProxyEnabled);
        Assert.Equal(10000, store.MixedPort);
        Assert.Equal(10000, viewModel.MixedPort);
        Assert.Equal(
            "RuntimeFailure.GeoData [geo.assets_missing]",
            viewModel.OperationErrorText);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Same(failure, error.Exception);
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

    /// <summary>Verifies failed sampling restart restores both persisted and displayed applied settings.</summary>
    [Fact]
    public async Task SamplingSettings_WhenRestartFails_RestoreAppliedSettings()
    {
        FakeSettingsStore store = new()
        {
            ConnectionSamplingEnabled = true,
            ConnectionSamplingIntervalSeconds = 30,
        };
        FakeApplicationErrorSink errorSink = new();
        InvalidOperationException failure = new("sampling restart failed");
        SettingsViewModel viewModel = CreateRuntimeMutationViewModel(
            store,
            restartConnectionSamplingAsync: async _ =>
            {
                await Task.Yield();
                throw failure;
            },
            errorSink: errorSink);

        viewModel.SetConnectionSamplingEnabled(false);
        await Assert.IsAssignableFrom<Task>(viewModel.RestartConnectionSamplingCommand.ExecutionTask);

        Assert.True(store.ConnectionSamplingEnabled);
        Assert.Equal(30, store.ConnectionSamplingIntervalSeconds);
        Assert.True(viewModel.ConnectionSamplingEnabled);
        Assert.Equal(30, viewModel.ConnectionSamplingIntervalSeconds);
        Assert.True(viewModel.HasOperationError);
        Assert.Equal("Application.UnexpectedError", viewModel.OperationErrorText);
        ApplicationError error = Assert.Single(errorSink.Errors);
        Assert.Equal("settings-connection-sampling", error.OperationName);
        Assert.Same(failure, error.Exception);
    }

    /// <summary>Verifies sampling changes made during restart are coalesced without losing the tracked task.</summary>
    [Fact]
    public async Task SamplingSettings_WhileRestartRuns_CoalesceLatestSettings()
    {
        FakeSettingsStore store = new()
        {
            ConnectionSamplingEnabled = true,
            ConnectionSamplingIntervalSeconds = 30,
        };
        TaskCompletionSource firstRestartStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource releaseFirstRestart = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<(bool Enabled, int IntervalSeconds)> appliedSettings = [];
        SettingsViewModel viewModel = CreateRuntimeMutationViewModel(
            store,
            restartConnectionSamplingAsync: async _ =>
            {
                appliedSettings.Add((store.ConnectionSamplingEnabled, store.ConnectionSamplingIntervalSeconds));
                if (appliedSettings.Count == 1)
                {
                    firstRestartStarted.TrySetResult();
                    await releaseFirstRestart.Task;
                }
            });

        viewModel.SetConnectionSamplingEnabled(false);
        Task trackedRestart = Assert.IsAssignableFrom<Task>(viewModel.RestartConnectionSamplingCommand.ExecutionTask);
        await firstRestartStarted.Task;
        viewModel.SetConnectionSamplingIntervalSeconds(60);

        Assert.Same(trackedRestart, viewModel.RestartConnectionSamplingCommand.ExecutionTask);
        releaseFirstRestart.TrySetResult();
        await trackedRestart;

        Assert.Equal([(false, 30), (false, 60)], appliedSettings);
        Assert.False(store.ConnectionSamplingEnabled);
        Assert.Equal(60, store.ConnectionSamplingIntervalSeconds);
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
        viewModel.Load();
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.StartupConflictCheckEnabled = false;

        Assert.False(store.StartupConflictCheckEnabled);
        Assert.False(viewModel.StartupConflictCheckEnabled);
        Assert.Contains(nameof(SettingsViewModel.StartupConflictCheckEnabled), changedProperties);
    }

    /// <summary>Verifies notification enablement and level are persisted through the settings view model.</summary>
    [Fact]
    public void NotificationSettings_PersistEnablementAndLevel()
    {
        FakeSettingsStore store = new()
        {
            NotificationEnabled = true,
            NotificationLevel = NotificationLevel.Default,
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });
        viewModel.Load();
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        viewModel.NotificationEnabled = false;
        bool levelChanged = viewModel.SetNotificationLevelIndex((int)NotificationLevel.More);

        Assert.True(levelChanged);
        Assert.False(store.NotificationEnabled);
        Assert.False(viewModel.NotificationEnabled);
        Assert.Equal(NotificationLevel.More, store.NotificationLevel);
        Assert.Equal(NotificationLevel.More, viewModel.NotificationLevel);
        Assert.Contains(nameof(SettingsViewModel.NotificationEnabled), changedProperties);
        Assert.Contains(nameof(SettingsViewModel.NotificationLevelIndex), changedProperties);
    }

    /// <summary>Verifies trigger settings persist independently from notification verbosity.</summary>
    [Fact]
    public void TriggerSettings_PersistEnablementAndNotificationSwitch()
    {
        FakeSettingsStore store = new()
        {
            TriggersEnabled = true,
            TriggerNotificationsEnabled = true,
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        viewModel.TriggersEnabled = false;
        viewModel.TriggerNotificationsEnabled = false;

        Assert.False(store.TriggersEnabled);
        Assert.False(store.TriggerNotificationsEnabled);
        Assert.False(viewModel.TriggersEnabled);
        Assert.False(viewModel.TriggerNotificationsEnabled);
    }

    /// <summary>Verifies trigger enablement is a live setting and does not mark the settings page as restart-pending.</summary>
    [Fact]
    public void TriggerSettings_DoNotRequireRestart()
    {
        FakeSettingsStore store = new()
        {
            TriggersEnabled = true,
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        viewModel.TriggersEnabled = false;

        Assert.False(viewModel.IsTriggerEngineRestartPending);
        Assert.False(viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Verifies tray settings persist close behavior, inactive icon behavior, and visible feature ids.</summary>
    [Fact]
    public void TraySettings_PersistCloseBehaviorAndVisibleFeatures()
    {
        FakeSettingsStore store = new()
        {
            CloseBehaviorMode = CloseBehaviorMode.MinimizeToTray,
            TrayVisibleFeatureIds = "status,mode,pages",
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = viewModel.SetCloseBehaviorModeIndex((int)CloseBehaviorMode.ConfirmExit);
        viewModel.TrayUseMonochromeInactiveIcon = false;
        viewModel.SetTrayVisibleFeatureIds(["pages", "safe-exit"]);

        Assert.True(changed);
        Assert.Equal(CloseBehaviorMode.ConfirmExit, store.CloseBehaviorMode);
        Assert.Equal(CloseBehaviorMode.ConfirmExit, viewModel.CloseBehaviorMode);
        Assert.False(store.TrayUseMonochromeInactiveIcon);
        Assert.Equal("pages,safe-exit", store.TrayVisibleFeatureIds);
        Assert.Equal("pages,safe-exit", viewModel.TrayVisibleFeatureIds);
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
        Assert.Equal(3, viewModel.CloseBehaviorModeOptions.Count);
        Assert.All(viewModel.CloseBehaviorModeOptions, Assert.NotEmpty);
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
        viewModel.Load();

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
        viewModel.Load();

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
        viewModel.Load();

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

    /// <summary>Verifies mainland China display and URL blocking changes are surfaced as restart-required settings.</summary>
    [Fact]
    public void MainlandChinaSettings_RestartPendingTracksDisplayAndUrlBlockingChanges()
    {
        FakeSettingsStore store = new()
        {
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementOnly,
            MainlandChinaUrlBlockingEnabled = false,
        };
        SettingsViewModel viewModel = new(store, _ => { }, () => { }, key => key);
        viewModel.Load();

        viewModel.SetMainlandChinaFeatureModeIndex((int)MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter);

        Assert.True(ReadProperty<bool>(viewModel, "IsMainlandChinaDisplayRestartPending"));
        Assert.True(ReadProperty<bool>(viewModel, "HasRestartRequiredSettings"));
        Assert.Equal("Settings.MainlandChinaDisplay.Title*", ReadProperty<string>(viewModel, "MainlandChinaDisplayTitleText"));

        viewModel.SetMainlandChinaFeatureModeIndex((int)MainlandChinaFeatureMode.FlagReplacementOnly);

        Assert.False(ReadProperty<bool>(viewModel, "IsMainlandChinaDisplayRestartPending"));
        Assert.False(ReadProperty<bool>(viewModel, "HasRestartRequiredSettings"));

        viewModel.MainlandChinaUrlBlockingEnabled = true;

        Assert.True(ReadProperty<bool>(viewModel, "IsMainlandChinaDisplayRestartPending"));
        Assert.Equal("Settings.MainlandChinaUrlBlocking.Title*", ReadProperty<string>(viewModel, "MainlandChinaUrlBlockingTitleText"));
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

        Assert.Equal("Settings.BackupRestore.Title", ReadProperty<string>(viewModel, "DataPackageTitleText"));
        Assert.Equal("Settings.BackupRestore.Description", ReadProperty<string>(viewModel, "DataPackageDescriptionText"));
        Assert.Equal("Settings.DataExport.Title", ReadProperty<string>(viewModel, "DataExportTitleText"));
        Assert.Equal("Settings.DataExport.Description", ReadProperty<string>(viewModel, "DataExportDescriptionText"));
        Assert.Equal("Command.Export", ReadProperty<string>(viewModel, "ExportText"));
        Assert.Equal("Command.Import", ReadProperty<string>(viewModel, "ImportText"));
    }

    /// <summary>Verifies known connection-test URLs are summarized with localized provider names.</summary>
    [Fact]
    public void ConnectionTestUrlSummary_UsesKnownLocalizedProviderNames()
    {
        SettingsViewModel defaultViewModel = new(new FakeSettingsStore(), _ => { }, () => { }, key => key);
        defaultViewModel.Load();

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
        viewModel.Load();

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
        viewModel.Load();

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

        object report = await InvokeRunConnectionTestReportAsync(viewModel, CancellationToken.None);
        IReadOnlyList<object> rows = ReadObjectProperty<IReadOnlyList<object>>(report, "Results");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.True(ReadObjectProperty<bool>(row, "Succeeded")));
        Assert.All(rows, row => Assert.Equal("HTTP 204", ReadObjectProperty<string>(row, "StatusText")));
        Assert.Equal("Settings.ConnectionTest.AllPassed", ReadObjectProperty<string>(report, "SummaryText"));
        Assert.Equal("AllPassed", ReadObjectProperty<object>(report, "SummaryState").ToString());
        Assert.Equal(
            ["https://www.google.com/", "https://github.com/", "https://www.baidu.com/"],
            requestedUris.Select(uri => uri.ToString()).ToArray());
        Assert.False(ReadProperty<bool>(viewModel, "IsConnectionTestRunning"));
    }

    /// <summary>Verifies connection testing returns table-ready rows with URL, status, latency, and a summary.</summary>
    [Fact]
    public async Task RunConnectionTestAsync_ReturnsStructuredRowsWithSummary()
    {
        List<Uri> requestedUris = [];
        SettingsViewModel viewModel = CreateConnectionTestViewModel(async (uri, _) =>
        {
            requestedUris.Add(uri);
            await Task.Delay(5, CancellationToken.None);
            return uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase) ? 302 : 204;
        });

        object report = await InvokeRunConnectionTestReportAsync(viewModel, CancellationToken.None);
        IReadOnlyList<object> rows = ReadObjectProperty<IReadOnlyList<object>>(report, "Results");

        Assert.Equal(3, rows.Count);
        Assert.Equal("https://www.google.com", ReadObjectProperty<string>(rows[0], "Url"));
        Assert.Equal("Settings.ConnectionTestUrl.Proxy1", ReadObjectProperty<string>(rows[0], "Label"));
        Assert.True(ReadObjectProperty<bool>(rows[0], "Succeeded"));
        Assert.Equal("HTTP 204", ReadObjectProperty<string>(rows[0], "StatusText"));
        Assert.EndsWith(" ms", ReadObjectProperty<string>(rows[0], "LatencyText"), StringComparison.Ordinal);
        Assert.Equal("Settings.ConnectionTest.AllPassed", ReadObjectProperty<string>(report, "SummaryText"));
        Assert.Equal("AllPassed", ReadObjectProperty<object>(report, "SummaryState").ToString());
        Assert.Equal(
            ["https://www.google.com/", "https://github.com/", "https://www.baidu.com/"],
            requestedUris.Select(uri => uri.ToString()).ToArray());
        Assert.False(ReadProperty<bool>(viewModel, "IsConnectionTestRunning"));
    }

    /// <summary>Verifies connection testing appends one searchable diagnostic log with every tested target.</summary>
    [Fact]
    public async Task RunConnectionTestAsync_AppendsSummaryLogWithTargetDetails()
    {
        List<LogEntry> logs = [];
        SettingsViewModel viewModel = CreateConnectionTestViewModel(
            async (_, _) =>
            {
                await Task.Delay(5, CancellationToken.None);
                return 204;
            },
            (level, category, message, detail) => logs.Add(new LogEntry(level, category, message, detail)));

        await InvokeRunConnectionTestReportAsync(viewModel, CancellationToken.None);

        LogEntry entry = Assert.Single(logs);
        Assert.Equal("Info", entry.Level);
        Assert.Equal("ConnectionTest", entry.Category);
        Assert.Equal("Settings.ConnectionTest.AllPassed", entry.Message);
        Assert.Contains("HTTP 204", entry.Detail);
        Assert.Contains("Settings.ConnectionTestUrl.Direct", entry.Detail);
        Assert.DoesNotContain("https://", entry.Detail, StringComparison.Ordinal);
    }

    /// <summary>Verifies a mixed connection-test result reports a partial-failure summary state.</summary>
    [Fact]
    public async Task RunConnectionTestAsync_OneProbeFails_ReturnsPartialSummaryState()
    {
        SettingsViewModel viewModel = CreateConnectionTestViewModel(async (uri, _) =>
        {
            await Task.Yield();
            return uri.Host.Contains("github", StringComparison.OrdinalIgnoreCase) ? 500 : 204;
        });

        object report = await InvokeRunConnectionTestReportAsync(viewModel, CancellationToken.None);
        IReadOnlyList<object> rows = ReadObjectProperty<IReadOnlyList<object>>(report, "Results");

        Assert.Equal(3, rows.Count);
        Assert.Equal([true, false, true], rows.Select(row => ReadObjectProperty<bool>(row, "Succeeded")).ToArray());
        Assert.Equal("2/3", ReadObjectProperty<string>(report, "SummaryText"));
        Assert.Equal("PartialFailed", ReadObjectProperty<object>(report, "SummaryState").ToString());
        Assert.False(ReadProperty<bool>(viewModel, "IsConnectionTestRunning"));
    }

    /// <summary>Verifies connection test failures are caught and returned as localized messages.</summary>
    [Fact]
    public async Task RunConnectionTestAsync_ProbeFails_ReturnsLocalizedFailureMessage()
    {
        SettingsViewModel viewModel = CreateConnectionTestViewModel((_, _) => throw new HttpRequestException("network unavailable"));

        object report = await InvokeRunConnectionTestReportAsync(viewModel, CancellationToken.None);
        IReadOnlyList<object> rows = ReadObjectProperty<IReadOnlyList<object>>(report, "Results");

        Assert.Equal(3, rows.Count);
        Assert.All(rows, row => Assert.False(ReadObjectProperty<bool>(row, "Succeeded")));
        Assert.All(
            rows,
            row => Assert.Equal(
                "failed Application.UnexpectedError",
                ReadObjectProperty<string>(row, "StatusText")));
        Assert.Equal("Settings.ConnectionTest.AllFailed", ReadObjectProperty<string>(report, "SummaryText"));
        Assert.Equal("AllFailed", ReadObjectProperty<object>(report, "SummaryState").ToString());
        Assert.False(ReadProperty<bool>(viewModel, "IsConnectionTestRunning"));
    }

    /// <summary>Verifies reset-all converges every external participant to the durable defaults.</summary>
    [Fact]
    public async Task ResetAllSettings_AppliesEveryExternalParticipantAndReloadsDefaults()
    {
        FakeSettingsStore store = new()
        {
            DisplayLanguage = AppLanguage.German,
            AppThemeMode = AppThemeMode.Dark,
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF00AA00",
            LaunchAtStartupEnabled = true,
            ConnectionSamplingEnabled = false,
            ConnectionSamplingIntervalSeconds = 90,
            CurrentMode = ClashSharpMode.RuleTakeover,
            ActiveProfileId = "profile-before-reset",
            TransparentProxyEnabled = false,
            MixedPort = 7890,
            ConnectionTestUrl = "https://example.com/old",
        };
        bool resetCalled = false;
        List<AppLanguage> languages = [];
        List<AppThemeMode> themes = [];
        List<(AppAccentColorMode Mode, string Value)> accents = [];
        List<bool> startupRegistrations = [];
        List<(bool Enabled, int IntervalSeconds)> samplingStates = [];
        List<(bool TunEnabled, int MixedPort)> networkStates = [];
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            languages.Add,
            resetAllSettings: () =>
            {
                resetCalled = true;
                ResetExternalSettingsToDefaults(store);
                store.ConnectionTestUrl = "https://example.com/reset";
            },
            clearAllData: () => { },
            applyTheme: themes.Add,
            applyAccentColor: (mode, value) => accents.Add((mode, value)),
            restartConnectionSamplingAsync: _ =>
            {
                samplingStates.Add((store.ConnectionSamplingEnabled, store.ConnectionSamplingIntervalSeconds));
                return Task.CompletedTask;
            },
            applyLaunchAtStartupAsync: (enabled, _) =>
            {
                startupRegistrations.Add(enabled);
                return Task.CompletedTask;
            },
            applyNetworkSettingsAsync: (tunEnabled, mixedPort, _) =>
            {
                networkStates.Add((tunEnabled, mixedPort));
                return Task.CompletedTask;
            });

        await viewModel.ResetAllSettingsAsync(CancellationToken.None);

        Assert.True(resetCalled);
        Assert.Equal([AppLanguage.AutoDetect], languages);
        Assert.Equal([AppThemeMode.FollowSystem], themes);
        Assert.Equal([(AppAccentColorMode.FollowSystem, "#FF0078D4")], accents);
        Assert.Equal([false], startupRegistrations);
        Assert.Equal([(true, 30)], samplingStates);
        Assert.Equal([(true, 10000)], networkStates);
        Assert.Equal(AppLanguage.AutoDetect, viewModel.DisplayLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, viewModel.AppThemeMode);
        Assert.Equal(AppAccentColorMode.FollowSystem, viewModel.AppAccentColorMode);
        Assert.False(viewModel.LaunchAtStartupEnabled);
        Assert.True(viewModel.ConnectionSamplingEnabled);
        Assert.Equal(30, viewModel.ConnectionSamplingIntervalSeconds);
        Assert.True(viewModel.TransparentProxyEnabled);
        Assert.Equal(10000, viewModel.MixedPort);
        Assert.Equal("https://example.com/reset", viewModel.ConnectionTestUrl);
        Assert.False(viewModel.IsResetRecoveryRequired);
        Assert.False(viewModel.HasOperationError);
    }

    /// <summary>Verifies caller cancellation cannot interrupt participants after durable reset begins.</summary>
    [Fact]
    public async Task ResetAllSettings_CallerCancelsAfterDurableReset_CompletesWithNonCancelableParticipants()
    {
        FakeSettingsStore store = new()
        {
            DisplayLanguage = AppLanguage.French,
            LaunchAtStartupEnabled = true,
            TransparentProxyEnabled = false,
            MixedPort = 7890,
        };
        using CancellationTokenSource cancellation = new();
        List<string> participants = [];
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => participants.Add("language"),
            resetAllSettings: () =>
            {
                ResetExternalSettingsToDefaults(store);
                cancellation.Cancel();
            },
            clearAllData: () => { },
            applyTheme: _ => participants.Add("theme"),
            applyAccentColor: (_, _) => participants.Add("accent"),
            applyLaunchAtStartupAsync: (_, token) =>
            {
                Assert.False(token.CanBeCanceled);
                participants.Add("startup");
                return Task.CompletedTask;
            },
            restartConnectionSamplingAsync: token =>
            {
                Assert.False(token.CanBeCanceled);
                participants.Add("sampling");
                return Task.CompletedTask;
            },
            applyNetworkSettingsAsync: (_, _, token) =>
            {
                Assert.False(token.CanBeCanceled);
                participants.Add("network");
                return Task.CompletedTask;
            });

        await viewModel.ResetAllSettingsAsync(cancellation.Token);

        Assert.True(cancellation.IsCancellationRequested);
        Assert.Equal(["language", "theme", "accent", "startup", "sampling", "network"], participants);
        Assert.False(viewModel.IsResetRecoveryRequired);
    }

    /// <summary>Verifies a participant failure restores and re-applies the previous external baseline.</summary>
    [Fact]
    public async Task ResetAllSettings_ParticipantFails_CompensatesEveryExternalSetting()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        store.ConnectionTestUrl = "https://example.com/before";
        List<bool> startupRegistrations = [];
        List<(bool TunEnabled, int MixedPort)> networkStates = [];
        bool restartRequested = false;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () =>
            {
                ResetExternalSettingsToDefaults(store);
                store.ConnectionTestUrl = "https://example.com/reset";
            },
            clearAllData: () => { },
            restartApplication: () => restartRequested = true,
            applyLaunchAtStartupAsync: (enabled, _) =>
            {
                startupRegistrations.Add(enabled);
                return !enabled && startupRegistrations.Count == 1
                    ? Task.FromException(new InvalidOperationException("default startup registration failed"))
                    : Task.CompletedTask;
            },
            applyNetworkSettingsAsync: (tunEnabled, mixedPort, _) =>
            {
                networkStates.Add((tunEnabled, mixedPort));
                return Task.CompletedTask;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.ResetAllSettingsAsync(CancellationToken.None));

        Assert.Equal([false, true], startupRegistrations);
        Assert.Equal([(true, 10000), (false, 7890)], networkStates);
        AssertExternalSettingsMatchNonDefaultBaseline(store);
        Assert.Equal("https://example.com/reset", store.ConnectionTestUrl);
        Assert.False(restartRequested);
        Assert.False(viewModel.IsResetRecoveryRequired);
        Assert.True(viewModel.HasOperationError);
    }

    /// <summary>Verifies production-style reset rollback restores the complete retained settings generation.</summary>
    [Fact]
    public async Task ResetAllSettings_ParticipantFails_RollsBackRetainedCompleteGeneration()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        store.ConnectionTestUrl = "https://example.com/before";
        TrackingSettingsResetReceipt? receipt = null;
        int startupCalls = 0;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () => throw new InvalidOperationException("legacy reset must not run"),
            clearAllData: () => { },
            applyLaunchAtStartupAsync: (_, _) =>
            {
                startupCalls++;
                return startupCalls == 1
                    ? Task.FromException(new InvalidOperationException("activation failed"))
                    : Task.CompletedTask;
            },
            beginResetSettings: () =>
            {
                FakeSettingsStoreSnapshot baseline = FakeSettingsStoreSnapshot.Capture(store);
                ResetExternalSettingsToDefaults(store);
                store.ConnectionTestUrl = "https://example.com/reset";
                receipt = new TrackingSettingsResetReceipt(
                    rollback: () => baseline.Restore(store));
                return receipt;
            });

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.ResetAllSettingsAsync(CancellationToken.None));

        Assert.NotNull(receipt);
        Assert.Equal(0, receipt.CommitCalls);
        Assert.Equal(1, receipt.RollbackCalls);
        Assert.True(receipt.Disposed);
        Assert.Equal("https://example.com/before", store.ConnectionTestUrl);
        AssertExternalSettingsMatchNonDefaultBaseline(store);
    }

    /// <summary>Verifies cleanup failure after a commit decision never changes the reset to rollback.</summary>
    [Fact]
    public async Task ResetAllSettings_CommitCleanupFails_DoesNotRollbackActivatedDefaults()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        TrackingSettingsResetReceipt? receipt = null;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () => throw new InvalidOperationException("legacy reset must not run"),
            clearAllData: () => { },
            beginResetSettings: () =>
            {
                ResetExternalSettingsToDefaults(store);
                receipt = new TrackingSettingsResetReceipt(
                    commitFailure: new IOException("cleanup unavailable"));
                return receipt;
            });

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.ResetAllSettingsAsync(CancellationToken.None));

        Assert.Contains("could not be finalized", failure.Message, StringComparison.Ordinal);
        Assert.NotNull(receipt);
        Assert.Equal(2, receipt.CommitCalls);
        Assert.Equal(0, receipt.RollbackCalls);
        Assert.True(receipt.Disposed);
        Assert.Equal(AppLanguage.AutoDetect, store.DisplayLanguage);
        Assert.False(store.LaunchAtStartupEnabled);
        Assert.Equal(10000, store.MixedPort);
    }

    /// <summary>Verifies failed compensation exposes recovery state and requests a forced restart.</summary>
    [Fact]
    public async Task ResetAllSettings_CompensationFails_RequiresRecoveryAndRequestsRestart()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        bool restartRequested = false;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () => ResetExternalSettingsToDefaults(store),
            clearAllData: () => { },
            restartApplication: () => restartRequested = true,
            applyLaunchAtStartupAsync: (_, _) =>
                Task.FromException(new InvalidOperationException("startup registration unavailable")));

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.ResetAllSettingsAsync(CancellationToken.None));

        Assert.Contains("restart recovery is required", failure.Message, StringComparison.Ordinal);
        Assert.True(restartRequested);
        Assert.True(viewModel.IsResetRecoveryRequired);
        Assert.True(viewModel.HasOperationError);
    }

    /// <summary>Verifies a rejected mandatory restart remains visible in the recovery failure graph.</summary>
    [Fact]
    public async Task ResetAllSettings_RecoveryRestartRejected_ReportsTheRejectedRequest()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () => ResetExternalSettingsToDefaults(store),
            clearAllData: () => { },
            applyLaunchAtStartupAsync: (_, _) =>
                Task.FromException(new InvalidOperationException("startup registration unavailable")),
            requestResetRecoveryRestart: () => false);

        AggregateException failure = await Assert.ThrowsAsync<AggregateException>(
            () => viewModel.ResetAllSettingsAsync(CancellationToken.None));

        Assert.Contains(
            failure.Flatten().InnerExceptions,
            exception => exception.Message.Contains("restart request was rejected", StringComparison.Ordinal));
        Assert.True(viewModel.IsResetRecoveryRequired);
    }

    /// <summary>Verifies cancellation before the commit point leaves durable settings untouched.</summary>
    [Fact]
    public async Task ResetAllSettings_PreCanceled_DoesNotStartDurableReset()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        bool resetCalled = false;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () => resetCalled = true,
            clearAllData: () => { });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.ResetAllSettingsAsync(cancellation.Token));

        Assert.False(resetCalled);
        AssertExternalSettingsMatchNonDefaultBaseline(store);
    }

    /// <summary>Verifies a scheduled coalesced network write is drained before reset crosses its commit point.</summary>
    [Fact]
    public async Task ResetAllSettings_PendingNetworkRequeue_DrainsBeforeDurableReset()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        bool resetCalled = false;
        TaskCompletionSource pendingRequeue = new(TaskCreationOptions.RunContinuationsAsynchronously);
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () =>
            {
                resetCalled = true;
                ResetExternalSettingsToDefaults(store);
            },
            clearAllData: () => { });
        FieldInfo? requeueField = typeof(SettingsViewModel).GetField(
            "_networkSettingsRequeueTask",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(requeueField);
        requeueField.SetValue(viewModel, pendingRequeue.Task);

        Task reset = viewModel.ResetAllSettingsAsync(CancellationToken.None);
        await Task.Yield();
        Assert.False(resetCalled);

        pendingRequeue.SetResult();
        await reset;
        Assert.True(resetCalled);
    }

    /// <summary>Verifies reset owns one destructive runtime scope through commit and compensation participants.</summary>
    [Fact]
    public async Task ResetAllSettings_HoldsOneDestructiveRuntimeScopeAcrossEveryParticipant()
    {
        FakeSettingsStore store = CreateNonDefaultExternalSettings();
        TrackingDestructiveRuntimeScope? scope = null;
        int acquisitionCount = 0;
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => Assert.True(scope?.IsActive),
            resetAllSettings: () =>
            {
                Assert.True(scope?.IsActive);
                ResetExternalSettingsToDefaults(store);
            },
            clearAllData: () => { },
            applyTheme: _ => Assert.True(scope?.IsActive),
            applyAccentColor: (_, _) => Assert.True(scope?.IsActive),
            beginDestructiveRuntimeMutationAsync: cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                acquisitionCount++;
                scope = new TrackingDestructiveRuntimeScope
                {
                    Reset = () => ResetExternalSettingsToDefaults(store),
                };
                return ValueTask.FromResult<ISettingsDestructiveRuntimeScope>(scope);
            });

        await viewModel.ResetAllSettingsAsync(CancellationToken.None);

        Assert.Equal(1, acquisitionCount);
        Assert.NotNull(scope);
        Assert.False(scope.IsActive);
        Assert.Equal(["startup", "sampling", "network"], scope.Calls);
    }

    /// <summary>
    /// Verifies reset reload does not claim process-owned tray and display resources already use the new defaults.
    /// </summary>
    [Fact]
    public async Task ResetAllSettings_RetainsRestartBaselineForSettingsNotAppliedByTransaction()
    {
        FakeSettingsStore store = new()
        {
            TrayUseMonochromeInactiveIcon = true,
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled,
            MainlandChinaUrlBlockingEnabled = true,
        };
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () =>
            {
                ResetExternalSettingsToDefaults(store);
                store.TrayUseMonochromeInactiveIcon = false;
                store.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementAndTextCompletion;
                store.MainlandChinaUrlBlockingEnabled = false;
            },
            clearAllData: () => { });
        Assert.False(viewModel.HasRestartRequiredSettings);

        await viewModel.ResetAllSettingsAsync(CancellationToken.None);

        Assert.True(viewModel.IsTrayIconRestartPending);
        Assert.True(viewModel.IsMainlandChinaDisplayRestartPending);
        Assert.True(viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Verifies imported restart-bound values remain pending after the package runtime is activated.</summary>
    [Fact]
    public void ReloadAfterDataImport_RetainsProcessAppliedRestartBaseline()
    {
        FakeSettingsStore store = new()
        {
            TrayUseMonochromeInactiveIcon = true,
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled,
            MainlandChinaUrlBlockingEnabled = true,
        };
        SettingsViewModel viewModel = CreateMaintenanceViewModel(
            store,
            _ => { },
            resetAllSettings: () => { },
            clearAllData: () => { });
        store.TrayUseMonochromeInactiveIcon = false;
        store.MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagReplacementAndTextCompletion;
        store.MainlandChinaUrlBlockingEnabled = false;

        viewModel.ReloadAfterDataImport();

        Assert.True(viewModel.IsTrayIconRestartPending);
        Assert.True(viewModel.IsMainlandChinaDisplayRestartPending);
        Assert.True(viewModel.HasRestartRequiredSettings);
    }

    /// <summary>Verifies clear-all data delegates maintenance work and reloads the current settings snapshot.</summary>
    [Fact]
    public async Task ClearAllDataAsync_RunsInjectedMaintenanceAndReloadsSettings()
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

        await viewModel.ClearAllDataAsync(CancellationToken.None);

        Assert.True(clearCalled);
        Assert.Null(appliedLanguage);
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
            TriggersEnabled = false,
            TriggerNotificationsEnabled = false,
            CloseBehaviorMode = CloseBehaviorMode.ExitWithoutConfirmation,
            TrayUseMonochromeInactiveIcon = false,
            TrayVisibleFeatureIds = "status,settings",
            CheckStaleProxyOnStartup = false,
            RestoreProxyOnExit = false,
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
        viewModel.Load();

        InvokeMethod<object?>(viewModel, "ResetBasicSettingsToDefaults", Array.Empty<object>());

        Assert.Equal(AppLanguage.AutoDetect, store.DisplayLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, store.AppThemeMode);
        Assert.Equal(AppAccentColorMode.FollowSystem, store.AppAccentColorMode);
        Assert.Equal("#FF0078D4", store.AppAccentColorValue);
        Assert.Equal(CloseBehaviorMode.MinimizeToTray, store.CloseBehaviorMode);
        Assert.Null(appliedLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, appliedTheme);

        InvokeMethod<object?>(viewModel, "ResetStartupSettingsToDefaults", Array.Empty<object>());

        Assert.False(store.LaunchAtStartupEnabled);
        Assert.True(store.StartupConflictCheckEnabled);
        Assert.True(store.ShowStartupGuideOnStartup);
        Assert.Equal(StartupBehaviorMode.LastSetting, store.StartupBehaviorMode);
        Assert.False(appliedLaunch);

        InvokeMethod<object?>(viewModel, "ResetTriggerSettingsToDefaults", Array.Empty<object>());

        Assert.True(store.TriggersEnabled);
        Assert.True(store.TriggerNotificationsEnabled);

        InvokeMethod<object?>(viewModel, "ResetTraySettingsToDefaults", Array.Empty<object>());

        Assert.False(store.TrayUseMonochromeInactiveIcon);
        Assert.Contains("pages", store.TrayVisibleFeatureIds, StringComparison.Ordinal);

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

        InvokeMethod<object?>(viewModel, "ResetMainlandChinaSettingsToDefaults", Array.Empty<object>());

        Assert.Equal(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion, store.MainlandChinaFeatureMode);
        Assert.False(store.MainlandChinaUrlBlockingEnabled);
    }

    /// <summary>Verifies startup conflict checks are delegated through an injected checker using the current mixed port.</summary>
    [Fact]
    public async Task CheckStartupConflictsAsync_UsesInjectedCheckerAndMixedPort()
    {
        FakeSettingsStore store = new() { MixedPort = 12345 };
        int? checkedPort = null;
        StartupConflictIssue expectedIssue = new(
            StartupConflictKind.MixedPortOccupied,
            "Port occupied",
            "Port 12345 is occupied.",
            "Inspect",
            _ => Task.FromResult(new StartupConflictRepairResult(false, "No repair")));
        SettingsViewModel viewModel = CreateStartupConflictViewModel(store, (port, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            checkedPort = port;
            return Task.FromResult<IReadOnlyList<StartupConflictIssue>>([expectedIssue]);
        });

        IReadOnlyList<StartupConflictIssue> issues =
            await viewModel.CheckStartupConflictsAsync(CancellationToken.None);

        Assert.Equal(12345, checkedPort);
        StartupConflictIssue issue = Assert.Single(issues);
        Assert.Same(expectedIssue, issue);
    }

    [Fact]
    public async Task CheckStartupConflictsAsync_WhenPageLifetimeIsCanceled_PropagatesCancellation()
    {
        FakeSettingsStore store = new() { MixedPort = 12345 };
        SettingsViewModel viewModel = CreateStartupConflictViewModel(
            store,
            static (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<StartupConflictIssue>>([]);
            });
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => viewModel.CheckStartupConflictsAsync(cancellation.Token));
    }

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public AppLanguage DisplayLanguage { get; set; } = AppLanguage.AutoDetect;

        public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.FollowSystem;

        public AppAccentColorMode AppAccentColorMode { get; set; } = AppAccentColorMode.FollowSystem;

        public string AppAccentColorValue { get; set; } = "#FF0078D4";

        public bool LaunchAtStartupEnabled { get; set; }

        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        public string ActiveProfileId { get; set; } = ProfileCatalogIds.BuiltInDirect;

        public bool TransparentProxyEnabled { get; set; } = true;

        public int MixedPort { get; set; } = 10000;

        public bool ConnectionSamplingEnabled { get; set; } = true;

        public int ConnectionSamplingIntervalSeconds { get; set; } = 30;

        public bool StartupConflictCheckEnabled { get; set; } = true;

        public StartupBehaviorMode StartupBehaviorMode { get; set; } = StartupBehaviorMode.LastSetting;

        public bool ShowStartupGuideOnStartup { get; set; } = true;

        public bool TriggersEnabled { get; set; } = true;

        public bool TriggerNotificationsEnabled { get; set; } = true;

        public CloseBehaviorMode CloseBehaviorMode { get; set; } = CloseBehaviorMode.MinimizeToTray;

        public bool TrayUseMonochromeInactiveIcon { get; set; } = true;

        public string TrayVisibleFeatureIds { get; set; } = "status,mode,pages,transparent-proxy,settings,safe-exit";

        public bool CheckStaleProxyOnStartup { get; set; } = true;

        public bool RestoreProxyOnExit { get; set; } = true;

        public MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; } = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter;

        public bool MainlandChinaUrlBlockingEnabled { get; set; }

        public bool NotificationEnabled { get; set; } = true;

        public NotificationLevel NotificationLevel { get; set; } = NotificationLevel.Default;

        public string ConnectionTestUrl { get; set; } = "https://www.google.com/generate_204";

        public string ConnectionTestProxyUrl1 { get; set; } = "https://www.google.com";

        public string ConnectionTestProxyUrl2 { get; set; } = "https://github.com";

        public string ConnectionTestDirectUrl { get; set; } = "https://www.baidu.com";
    }

    private static SettingsViewModel CreateConnectionTestViewModel(
        Func<Uri, CancellationToken, Task<int>> testConnectionAsync,
        Action<string, string, string, string?>? appendLog = null)
    {
        SettingsViewModel viewModel = new(
            new FakeSettingsStore(),
            _ => { },
            _ => { },
            () => { },
            _ => { },
            key => key switch
            {
                "Settings.ConnectionTest.Succeeded.Format" => "success {0}",
                "Settings.ConnectionTest.Failed.Format" => "failed {0}",
                "Settings.ConnectionTest.StatusHttp.Format" => "HTTP {0}",
                "Settings.ConnectionTest.AllPassed" => "Settings.ConnectionTest.AllPassed",
                "Settings.ConnectionTest.AllFailed" => "Settings.ConnectionTest.AllFailed",
                "Settings.ConnectionTest.PartialPassed.Format" => "{0}/{1}",
                _ => key,
            },
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            new FakeApplicationErrorSink(),
            () => { },
            () => { },
            () => false,
            () => { },
            () => { },
            testConnectionAsync: testConnectionAsync,
            resetAllSettings: () => { },
            clearAllData: () => { },
            checkStartupConflictsAsync: NoStartupConflictsAsync,
            notifyConnectionTestTimeout: _ => { },
            appendLog: appendLog ?? ((_, _, _, _) => { }));
        viewModel.Load();
        return viewModel;
    }

    private sealed record LogEntry(string Level, string Category, string Message, string? Detail);

    private static Task<IReadOnlyList<StartupConflictIssue>> NoStartupConflictsAsync(
        int mixedPort,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<StartupConflictIssue>>([]);
    }

    private static SettingsViewModel CreateRuntimeMutationViewModel(
        FakeSettingsStore store,
        Func<CancellationToken, Task>? restartConnectionSamplingAsync = null,
        Func<bool, CancellationToken, Task>? applyLaunchAtStartupAsync = null,
        IApplicationErrorSink? errorSink = null,
        Func<bool, int, CancellationToken, Task>? applyNetworkSettingsAsync = null)
    {
        SettingsViewModel viewModel = new(
            store,
            _ => { },
            _ => { },
            () => { },
            _ => { },
            key => key,
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            errorSink ?? new FakeApplicationErrorSink(),
            () => { },
            () => { },
            () => false,
            () => { },
            () => { },
            (_, _) => Task.FromResult(204),
            restartConnectionSamplingAsync: restartConnectionSamplingAsync,
            applyLaunchAtStartupAsync: applyLaunchAtStartupAsync,
            applyNetworkSettingsAsync: applyNetworkSettingsAsync);
        viewModel.Load();
        return viewModel;
    }

    private sealed class FakeApplicationErrorSink : IApplicationErrorSink
    {
        public List<ApplicationError> Errors { get; } = [];

        public Task ReportAsync(ApplicationError applicationError, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Errors.Add(applicationError);
            return Task.CompletedTask;
        }
    }

    private static SettingsViewModel CreateMaintenanceViewModel(
        FakeSettingsStore store,
        Action<AppLanguage> applyLanguage,
        Action resetAllSettings,
        Action clearAllData,
        Action<AppThemeMode>? applyTheme = null,
        Action<AppAccentColorMode, string>? applyAccentColor = null,
        Func<CancellationToken, Task>? restartConnectionSamplingAsync = null,
        Func<bool, CancellationToken, Task>? applyLaunchAtStartupAsync = null,
        Func<bool, int, CancellationToken, Task>? applyNetworkSettingsAsync = null,
        Action? restartApplication = null,
        Func<bool>? requestResetRecoveryRestart = null,
        Func<CancellationToken, ValueTask<ISettingsDestructiveRuntimeScope>>?
            beginDestructiveRuntimeMutationAsync = null,
        Func<ISettingsResetTransactionReceipt>? beginResetSettings = null)
    {
        SettingsViewModel viewModel = new(
            store,
            applyLanguage,
            applyTheme ?? (_ => { }),
            () => { },
            _ => { },
            key => key,
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            new FakeApplicationErrorSink(),
            () => { },
            restartApplication ?? (() => { }),
            () => false,
            () => { },
            () => { },
            testConnectionAsync: (_, _) => Task.FromResult(204),
            applyAccentColor: applyAccentColor,
            resetAllSettings: resetAllSettings,
            clearAllData: clearAllData,
            checkStartupConflictsAsync: NoStartupConflictsAsync,
            notifyConnectionTestTimeout: _ => { },
            appendLog: (_, _, _, _) => { },
            restartConnectionSamplingAsync: restartConnectionSamplingAsync,
            applyLaunchAtStartupAsync: applyLaunchAtStartupAsync,
            applyNetworkSettingsAsync: applyNetworkSettingsAsync,
            requestResetRecoveryRestart: requestResetRecoveryRestart,
            beginDestructiveRuntimeMutationAsync: beginDestructiveRuntimeMutationAsync,
            beginResetSettings: beginResetSettings);
        viewModel.Load();
        return viewModel;
    }

    private static FakeSettingsStore CreateNonDefaultExternalSettings()
    {
        return new FakeSettingsStore
        {
            DisplayLanguage = AppLanguage.German,
            AppThemeMode = AppThemeMode.Dark,
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF00AA00",
            LaunchAtStartupEnabled = true,
            ConnectionSamplingEnabled = false,
            ConnectionSamplingIntervalSeconds = 90,
            CurrentMode = ClashSharpMode.RuleTakeover,
            ActiveProfileId = "profile-before-reset",
            TransparentProxyEnabled = false,
            MixedPort = 7890,
        };
    }

    private sealed class TrackingDestructiveRuntimeScope : ISettingsDestructiveRuntimeScope
    {
        public Action? Reset { get; init; }

        public bool IsActive { get; private set; } = true;

        public List<string> Calls { get; } = [];

        public Task<ISettingsDataPackageTransactionReceipt> BeginImportAsync(
            string packagePath,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public ISettingsResetTransactionReceipt BeginResetSettings()
        {
            Assert.True(IsActive);
            Reset?.Invoke();
            return new TrackingSettingsResetReceipt();
        }

        public void RestoreDurableSettings(SettingsExternalDurableSnapshot snapshot)
        {
            Assert.True(IsActive);
        }

        public Task ApplyLaunchAtStartupAsync(bool isEnabled, CancellationToken cancellationToken)
        {
            _ = isEnabled;
            AssertActive(cancellationToken);
            Calls.Add("startup");
            return Task.CompletedTask;
        }

        public Task RestartConnectionSamplingAsync(CancellationToken cancellationToken)
        {
            AssertActive(cancellationToken);
            Calls.Add("sampling");
            return Task.CompletedTask;
        }

        public Task ApplyNetworkSettingsAsync(
            bool transparentProxyEnabled,
            int mixedPort,
            CancellationToken cancellationToken)
        {
            _ = transparentProxyEnabled;
            _ = mixedPort;
            AssertActive(cancellationToken);
            Calls.Add("network");
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Assert.True(IsActive);
            IsActive = false;
            return ValueTask.CompletedTask;
        }

        private void AssertActive(CancellationToken cancellationToken)
        {
            Assert.True(IsActive);
            Assert.False(cancellationToken.CanBeCanceled);
        }
    }

    private sealed class TrackingSettingsResetReceipt(
        Action? rollback = null,
        Exception? commitFailure = null) : ISettingsResetTransactionReceipt
    {
        public int CommitCalls { get; private set; }

        public int RollbackCalls { get; private set; }

        public bool Disposed { get; private set; }

        public Task CommitAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CommitCalls++;
            return commitFailure is null
                ? Task.CompletedTask
                : Task.FromException(commitFailure);
        }

        public Task RollbackAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RollbackCalls++;
            rollback?.Invoke();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private sealed record FakeSettingsStoreSnapshot(
        AppLanguage DisplayLanguage,
        AppThemeMode AppThemeMode,
        AppAccentColorMode AppAccentColorMode,
        string AppAccentColorValue,
        bool LaunchAtStartupEnabled,
        bool ConnectionSamplingEnabled,
        int ConnectionSamplingIntervalSeconds,
        ClashSharpMode CurrentMode,
        string ActiveProfileId,
        bool TransparentProxyEnabled,
        int MixedPort,
        string ConnectionTestUrl)
    {
        public static FakeSettingsStoreSnapshot Capture(FakeSettingsStore store) => new(
            store.DisplayLanguage,
            store.AppThemeMode,
            store.AppAccentColorMode,
            store.AppAccentColorValue,
            store.LaunchAtStartupEnabled,
            store.ConnectionSamplingEnabled,
            store.ConnectionSamplingIntervalSeconds,
            store.CurrentMode,
            store.ActiveProfileId,
            store.TransparentProxyEnabled,
            store.MixedPort,
            store.ConnectionTestUrl);

        public void Restore(FakeSettingsStore store)
        {
            store.DisplayLanguage = DisplayLanguage;
            store.AppThemeMode = AppThemeMode;
            store.AppAccentColorMode = AppAccentColorMode;
            store.AppAccentColorValue = AppAccentColorValue;
            store.LaunchAtStartupEnabled = LaunchAtStartupEnabled;
            store.ConnectionSamplingEnabled = ConnectionSamplingEnabled;
            store.ConnectionSamplingIntervalSeconds = ConnectionSamplingIntervalSeconds;
            store.CurrentMode = CurrentMode;
            store.ActiveProfileId = ActiveProfileId;
            store.TransparentProxyEnabled = TransparentProxyEnabled;
            store.MixedPort = MixedPort;
            store.ConnectionTestUrl = ConnectionTestUrl;
        }
    }

    private static void ResetExternalSettingsToDefaults(FakeSettingsStore store)
    {
        store.DisplayLanguage = AppLanguage.AutoDetect;
        store.AppThemeMode = AppThemeMode.FollowSystem;
        store.AppAccentColorMode = AppAccentColorMode.FollowSystem;
        store.AppAccentColorValue = "#FF0078D4";
        store.LaunchAtStartupEnabled = false;
        store.ConnectionSamplingEnabled = true;
        store.ConnectionSamplingIntervalSeconds = 30;
        store.CurrentMode = ClashSharpMode.Disabled;
        store.ActiveProfileId = ProfileCatalogIds.BuiltInDirect;
        store.TransparentProxyEnabled = true;
        store.MixedPort = 10000;
    }

    private static void AssertExternalSettingsMatchNonDefaultBaseline(FakeSettingsStore store)
    {
        Assert.Equal(AppLanguage.German, store.DisplayLanguage);
        Assert.Equal(AppThemeMode.Dark, store.AppThemeMode);
        Assert.Equal(AppAccentColorMode.Custom, store.AppAccentColorMode);
        Assert.Equal("#FF00AA00", store.AppAccentColorValue);
        Assert.True(store.LaunchAtStartupEnabled);
        Assert.False(store.ConnectionSamplingEnabled);
        Assert.Equal(90, store.ConnectionSamplingIntervalSeconds);
        Assert.Equal(ClashSharpMode.RuleTakeover, store.CurrentMode);
        Assert.Equal("profile-before-reset", store.ActiveProfileId);
        Assert.False(store.TransparentProxyEnabled);
        Assert.Equal(7890, store.MixedPort);
    }

    private static SettingsViewModel CreateStartupConflictViewModel(
        FakeSettingsStore store,
        Func<int, CancellationToken, Task<IReadOnlyList<StartupConflictIssue>>> checkStartupConflictsAsync)
    {
        SettingsViewModel viewModel = new(
            store,
            _ => { },
            _ => { },
            () => { },
            _ => { },
            key => key,
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            new FakeApplicationErrorSink(),
            () => { },
            () => { },
            () => false,
            () => { },
            () => { },
            testConnectionAsync: (_, _) => Task.FromResult(204),
            resetAllSettings: () => { },
            clearAllData: () => { },
            checkStartupConflictsAsync: checkStartupConflictsAsync,
            notifyConnectionTestTimeout: _ => { },
            appendLog: (_, _, _, _) => { });
        viewModel.Load();
        return viewModel;
    }

    private static SettingsViewModel CreateAccentRestartViewModel(
        FakeSettingsStore store,
        Func<AppAccentColorMode, string, bool> isAccentColorRestartPending)
    {
        SettingsViewModel viewModel = new(
            store,
            _ => { },
            _ => { },
            () => { },
            _ => { },
            key => key,
            () => new SettingsProxyInformation("config.yaml", true, "mihomo.exe"),
            new FakeApplicationErrorSink(),
            () => { },
            () => { },
            () => false,
            () => { },
            () => { },
            testConnectionAsync: (_, _) => Task.FromResult(204),
            resetAllSettings: () => { },
            clearAllData: () => { },
            checkStartupConflictsAsync: NoStartupConflictsAsync,
            isAccentColorRestartPending: isAccentColorRestartPending,
            notifyConnectionTestTimeout: _ => { },
            appendLog: (_, _, _, _) => { });
        viewModel.Load();
        return viewModel;
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

    private static async Task<object> InvokeRunConnectionTestReportAsync(SettingsViewModel viewModel, CancellationToken cancellationToken)
    {
        MethodInfo? method = typeof(SettingsViewModel).GetMethod("RunConnectionTestAsync");
        Assert.NotNull(method);
        Type? reportType = typeof(SettingsViewModel).Assembly.GetType("ClashSharp.ViewModel.ConnectionTestReport");
        Assert.NotNull(reportType);
        object? value = method.Invoke(viewModel, [cancellationToken]);
        Type expectedTaskType = typeof(Task<>).MakeGenericType(reportType);
        Assert.IsAssignableFrom(expectedTaskType, value);
        await (Task)value!;
        object? result = value.GetType().GetProperty("Result")?.GetValue(value);
        Assert.IsType(reportType, result);
        return result!;
    }

    private static T ReadObjectProperty<T>(object value, string propertyName)
    {
        PropertyInfo? property = value.GetType().GetProperty(propertyName);
        Assert.NotNull(property);
        return Assert.IsAssignableFrom<T>(property.GetValue(value));
    }

    /// <summary>Fake mihomo service controller for transparent proxy settings tests.</summary>
    private sealed class FakeMihomoServiceController : IMihomoServiceController
    {
        /// <summary>Initializes a fake controller with a starting status.</summary>
        /// <param name="status">Initial service status.</param>
        public FakeMihomoServiceController(MihomoServiceStatus status)
        {
            CurrentStatus = status;
        }

        /// <summary>Gets or sets current service status.</summary>
        /// <value>Current fake status.</value>
        public MihomoServiceStatus CurrentStatus { get; set; }

        /// <summary>Gets current fake service status.</summary>
        /// <returns>Current fake status.</returns>
        public MihomoServiceStatus GetLatestStatus()
        {
            return CurrentStatus;
        }

        public Task<MihomoServiceStatus> RefreshStatusAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentStatus);
        }

    }
}
