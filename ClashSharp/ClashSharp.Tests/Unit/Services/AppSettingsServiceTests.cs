using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests defaults exposed by application settings.</summary>
public sealed class AppSettingsServiceTests
{
    [Fact]
    public async Task OrdinarySetter_EnteredBeforeExclusiveClose_MustDrainBeforeExclusiveLease()
    {
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        ValueTask<MutationAdmissionLease> pendingExclusive = default;
        void OnChanged(object? sender, AppSettingChangedEventArgs change)
        {
            if (change.Key != "MixedPort")
            {
                return;
            }

            pendingExclusive = barrier.CloseAndDrainAsync(
                MutationAdmissionClosure.Destructive,
                CancellationToken.None);
            Assert.False(pendingExclusive.IsCompleted);
        }

        settings.SettingChanged += OnChanged;
        try
        {
            settings.MixedPort = 12003;
            await using MutationAdmissionLease exclusive = await pendingExclusive;
            Assert.True(exclusive.IsExclusive);
            Assert.Equal(12003, settings.MixedPort);
        }
        finally
        {
            settings.SettingChanged -= OnChanged;
            if (barrier.State == MutationAdmissionState.Open)
            {
                settings.ResetAllSettings();
            }
        }
    }

    [Fact]
    public async Task ExclusiveAdmission_RejectsOrdinarySetter_ButAcceptsOneAdmittedBatch()
    {
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        await using (MutationAdmissionLease exclusive = await barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None))
        {
            Assert.Throws<MutationAdmissionRejectedException>(
                () => settings.MixedPort = 12004);

            settings.WriteAdmitted(exclusive, editor =>
            {
                editor.MixedPort = 12005;
                editor.ConnectionSamplingIntervalSeconds = 45;
            });

            Assert.Equal(12005, settings.MixedPort);
            Assert.Equal(45, settings.ConnectionSamplingIntervalSeconds);
        }

        settings.ResetAllSettings();
    }

    [Fact]
    public async Task AdmittedBatch_WhenValidationFails_DoesNotApplyAnyStagedKey()
    {
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        int baselinePort = settings.MixedPort;
        int baselineInterval = settings.ConnectionSamplingIntervalSeconds;
        await using (MutationAdmissionLease exclusive = await barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive,
            CancellationToken.None))
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                settings.WriteAdmitted(exclusive, editor =>
                {
                    editor.MixedPort = 12006;
                    editor.ConnectionSamplingIntervalSeconds = 301;
                }));

            Assert.Equal(baselinePort, settings.MixedPort);
            Assert.Equal(baselineInterval, settings.ConnectionSamplingIntervalSeconds);
        }

        settings.ResetAllSettings();
    }

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

    /// <summary>Verifies the built-in mainland China display defaults to flag replacement plus text completion.</summary>
    [Fact]
    public void MainlandChinaFeatureMode_DefaultsToFlagReplacementAndTextCompletion()
    {
        ResetSettings();

        Assert.Equal(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion, AppSettingsService.Instance.MainlandChinaFeatureMode);
    }

    /// <summary>Verifies the persisted hero layout default matches the effective eight-slot product layout.</summary>
    [Fact]
    public void MasterHeroStatusLayout_DefaultsToCanonicalProductLayout()
    {
        ResetSettings();

        Assert.Equal(
            "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability",
            AppSettingsService.Instance.MasterHeroStatusLayout);
    }

    /// <summary>Verifies direct hero layout writes use the same registry normalization as data packages.</summary>
    [Fact]
    public void MasterHeroStatusLayout_WhenPartial_PersistsCanonicalEightSlotLayout()
    {
        ResetSettings();
        try
        {
            AppSettingsService.Instance.MasterHeroStatusLayout = " Latency,CoreStatus,LATENCY ";

            Assert.Equal(
                "Latency,CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic",
                AppSettingsService.Instance.MasterHeroStatusLayout);
        }
        finally
        {
            ResetSettings();
        }
    }

    /// <summary>Verifies direct layout writes persist the registry's canonical text.</summary>
    [Fact]
    public void MasterInfoTileLayout_WhenNoncanonical_PersistsCanonicalText()
    {
        ResetSettings();
        try
        {
            AppSettingsService.Instance.MasterInfoTileLayout =
                " Latency, core,LATENCY, memory-usage ";

            Assert.Equal(
                "latency,core,memory-usage",
                AppSettingsService.Instance.MasterInfoTileLayout);
        }
        finally
        {
            ResetSettings();
        }
    }

    /// <summary>Verifies direct unsafe layout writes cannot replace the current persisted value.</summary>
    [Fact]
    public void MasterInfoTileLayout_WhenUnsafe_RejectsValueWithoutChangingSetting()
    {
        ResetSettings();
        try
        {
            AppSettingsService.Instance.MasterInfoTileLayout = "latency,core";

            Assert.Throws<ArgumentException>(
                () => AppSettingsService.Instance.MasterInfoTileLayout = "core,../unknown");

            Assert.Equal("latency,core", AppSettingsService.Instance.MasterInfoTileLayout);
        }
        finally
        {
            ResetSettings();
        }
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
        AppSettingsService.Instance.MasterHeroStatusLayout =
            "Latency,ActiveConnections,CurrentMode,ActiveProfile,MihomoService,StartupLaunch,SystemProxy,Availability";
        WriteShowStartupGuideOnStartup(false);

        AppSettingsService.Instance.ResetAllSettings();

        Assert.Equal(10000, AppSettingsService.Instance.MixedPort);
        Assert.Equal("https://www.google.com/generate_204", AppSettingsService.Instance.ConnectionTestUrl);
        Assert.False(AppSettingsService.Instance.MainlandChinaUrlBlockingEnabled);
        Assert.Equal(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion, AppSettingsService.Instance.MainlandChinaFeatureMode);
        Assert.Equal(AppLanguage.AutoDetect, AppSettingsService.Instance.DisplayLanguage);
        Assert.Equal(AppThemeMode.FollowSystem, AppSettingsService.Instance.AppThemeMode);
        Assert.Equal("FollowSystem", ReadAppAccentColorModeName());
        Assert.Equal("#FF0078D4", ReadAppAccentColorValue());
        Assert.False(AppSettingsService.Instance.LaunchAtStartupEnabled);
        Assert.Equal(StartupBehaviorMode.LastSetting, AppSettingsService.Instance.StartupBehaviorMode);
        Assert.True(AppSettingsService.Instance.StartupConflictCheckEnabled);
        Assert.Equal(
            "CoreStatus,SystemProxy,TransparentProxy,CurrentNode,UploadRate,DownloadRate,TotalTraffic,Availability",
            AppSettingsService.Instance.MasterHeroStatusLayout);
        Assert.True(ReadShowStartupGuideOnStartup());
    }

    /// <summary>Verifies ordinary reset preserves the internal credential while clear-all rotates it.</summary>
    [Fact]
    public void ClearAllSettings_RemovesInternalControllerCredential()
    {
        AppSettingsService.Instance.ClearAllSettings();
        string firstSecret = AppSettingsService.Instance.MihomoControllerSecret;

        AppSettingsService.Instance.ResetAllSettings();
        Assert.Equal(firstSecret, AppSettingsService.Instance.MihomoControllerSecret);

        AppSettingsService.Instance.ClearAllSettings();
        string rotatedSecret = AppSettingsService.Instance.MihomoControllerSecret;

        Assert.NotEqual(firstSecret, rotatedSecret);
    }

    /// <summary>Verifies settings writes expose one auditable change event and suppress no-op writes.</summary>
    [Fact]
    public void SettingChanged_RaisesForChangedValuesOnly()
    {
        ResetSettings();
        List<AppSettingChangedEventArgs> changes = [];
        void OnSettingChanged(object? sender, AppSettingChangedEventArgs e)
        {
            changes.Add(e);
        }

        AppSettingsService.Instance.SettingChanged += OnSettingChanged;
        try
        {
            AppSettingsService.Instance.MixedPort = 12001;
            AppSettingsService.Instance.MixedPort = 12001;
        }
        finally
        {
            AppSettingsService.Instance.SettingChanged -= OnSettingChanged;
            ResetSettings();
        }

        AppSettingChangedEventArgs change = Assert.Single(changes);
        Assert.Equal("MixedPort", change.Key);
        Assert.Equal(12001, change.NewValue);
        Assert.False(change.WasRemoved);
    }

    /// <summary>Verifies change subscribers can safely enter settings from another thread.</summary>
    [Fact]
    public void SettingChanged_IsRaisedAfterReleasingSettingsLock()
    {
        ResetSettings();
        void OnSettingChanged(object? sender, AppSettingChangedEventArgs e)
        {
            Task<int> concurrentRead = Task.Run(
                () => AppSettingsService.Instance.MixedPort);
            Assert.True(
                concurrentRead.Wait(TimeSpan.FromSeconds(5)),
                "A settings change subscriber was invoked while the settings lock was held.");
            Assert.Equal(12002, concurrentRead.GetAwaiter().GetResult());
        }

        AppSettingsService.Instance.SettingChanged += OnSettingChanged;
        try
        {
            AppSettingsService.Instance.MixedPort = 12002;
        }
        finally
        {
            AppSettingsService.Instance.SettingChanged -= OnSettingChanged;
            ResetSettings();
        }
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
