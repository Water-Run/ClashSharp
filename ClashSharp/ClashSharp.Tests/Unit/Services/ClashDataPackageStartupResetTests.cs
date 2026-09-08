using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class ClashDataPackageServiceTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StartupReset_RetainsTheFullSnapshotAndNotifiesOneCompleteAdmittedBatch(bool rollback)
    {
        using TemporaryDirectory directory = new();
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        ClashDataPackageSettingsAdapter adapter = new(settings);
        Dictionary<string, object?> defaults = CapturePackageSettingValues(adapter);
        SeedStartupResetSettings(settings);
        Dictionary<string, object?> before = CapturePackageSettingValues(adapter);
        Dictionary<string, object?> expected = StartupDefaultsFrom(before, defaults);
        List<Dictionary<string, object?>> observations = [];
        List<bool> ordinaryRejections = [];
        void Changed(object? sender, AppSettingChangedEventArgs change)
        {
            observations.Add(CapturePackageSettingValues(adapter));
            try
            {
                settings.MixedPort = 5555;
                ordinaryRejections.Add(false);
            }
            catch (MutationAdmissionRejectedException)
            {
                ordinaryRejections.Add(true);
            }
        }

        settings.SettingChanged += Changed;
        try
        {
            await using MutationAdmissionLease lease = await barrier.CloseAndDrainAsync(
                MutationAdmissionClosure.Destructive, CancellationToken.None);
            ClashDataPackageService service = new(adapter, directory.Path);
            await using DataPackageTransactionReceipt receipt = service.BeginResetStartupSettingsAdmitted(lease);

            Assert.Equal(expected.OrderBy(pair => pair.Key), CapturePackageSettingValues(adapter).OrderBy(pair => pair.Key));
            Assert.Equal(4, observations.Count);
            Assert.All(observations, state => Assert.Equal(expected.OrderBy(pair => pair.Key), state.OrderBy(pair => pair.Key)));
            Assert.All(ordinaryRejections, Assert.True);
            settings.SettingChanged -= Changed;

            if (rollback)
            {
                await receipt.RollbackAsync(CancellationToken.None);
            }
            else
            {
                await receipt.CommitAsync(CancellationToken.None);
            }

            Assert.Equal((rollback ? before : expected).OrderBy(pair => pair.Key),
                CapturePackageSettingValues(adapter).OrderBy(pair => pair.Key));
            AssertTransactionClean(directory.Path);
        }
        finally
        {
            settings.SettingChanged -= Changed;
            settings.ResetAllSettings();
        }
    }

    [Theory]
    [InlineData((int)DataPackageTransactionCheckpoint.ManifestPersisted, false)]
    [InlineData((int)DataPackageTransactionCheckpoint.ResetMutationCompleted, false)]
    [InlineData((int)DataPackageTransactionCheckpoint.ResetSettingsApplied, true)]
    public async Task StartupReset_NewServiceReplaysTheRetainedDecisionWithoutResettingOtherGroups(
        int crashAt,
        bool forward)
    {
        using TemporaryDirectory directory = new();
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        ClashDataPackageSettingsAdapter adapter = new(settings);
        Dictionary<string, object?> defaults = CapturePackageSettingValues(adapter);
        SeedStartupResetSettings(settings);
        Dictionary<string, object?> before = CapturePackageSettingValues(adapter);
        Dictionary<string, object?> expected = forward ? StartupDefaultsFrom(before, defaults) : before;
        try
        {
            await using MutationAdmissionLease lease = await barrier.CloseAndDrainAsync(
                MutationAdmissionClosure.Destructive, CancellationToken.None);
            ClashDataPackageService interrupted = new(adapter, directory.Path, checkpoint =>
            {
                if ((int)checkpoint == crashAt)
                {
                    throw new DataPackageSimulatedCrashException("Injected startup reset interruption.");
                }
            });
            Assert.Throws<DataPackageSimulatedCrashException>(() => interrupted.BeginResetStartupSettingsAdmitted(lease));

            ClashDataPackageService recovered = new(new ClashDataPackageSettingsAdapter(settings), directory.Path);
            await recovered.ReconcilePendingTransactionAdmittedAsync(lease, CancellationToken.None);
            await recovered.ReconcilePendingTransactionAdmittedAsync(lease, CancellationToken.None);

            Assert.Equal(expected.OrderBy(pair => pair.Key), CapturePackageSettingValues(adapter).OrderBy(pair => pair.Key));
            AssertTransactionClean(directory.Path);
        }
        finally
        {
            settings.ResetAllSettings();
        }
    }

    [Fact]
    public void StartupReset_RejectsOrdinaryAdmissionBeforeCreatingAJournal()
    {
        using TemporaryDirectory directory = new();
        MutationAdmissionBarrier barrier = new();
        FakeClashDataPackageSettings settings = new() { Admission = barrier, LaunchAtStartupEnabled = true };
        ClashDataPackageService service = new(settings, directory.Path);
        using MutationAdmissionLease ordinary = barrier.AcquireOrdinary();

        Assert.Throws<ArgumentException>(() => service.BeginResetStartupSettingsAdmitted(ordinary));

        Assert.True(settings.LaunchAtStartupEnabled);
        Assert.Equal(0, settings.AdmittedWriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    private static Dictionary<string, object?> CapturePackageSettingValues(IClashDataPackageSettings settings)
    {
        return typeof(IClashDataPackageSettings).GetProperties()
            .ToDictionary(property => property.Name, property => property.GetValue(settings), StringComparer.Ordinal);
    }

    private static Dictionary<string, object?> StartupDefaultsFrom(
        Dictionary<string, object?> baseline,
        Dictionary<string, object?> defaults)
    {
        Dictionary<string, object?> expected = new(baseline, StringComparer.Ordinal);
        string[] startupKeys =
        [
            nameof(IClashDataPackageSettings.LaunchAtStartupEnabled),
            nameof(IClashDataPackageSettings.StartupConflictCheckEnabled),
            nameof(IClashDataPackageSettings.ShowStartupGuideOnStartup),
            nameof(IClashDataPackageSettings.StartupBehaviorMode),
        ];
        Assert.Equal(startupKeys.Order(StringComparer.Ordinal),
            SettingsRegistry.Default.GetResetDefinitions(SettingsResetScope.Startup)
                .Select(definition => definition.Key.Value).Order(StringComparer.Ordinal));
        foreach (string key in startupKeys)
        {
            expected[key] = defaults[key];
        }

        return expected;
    }

    private static void SeedStartupResetSettings(AppSettingsService settings)
    {
        settings.LaunchAtStartupEnabled = true;
        settings.StartupConflictCheckEnabled = false;
        settings.ShowStartupGuideOnStartup = false;
        settings.StartupBehaviorMode = StartupBehaviorMode.DisableProxy;
        settings.DisplayLanguage = AppLanguage.French;
        settings.AppThemeMode = AppThemeMode.Dark;
        settings.CurrentMode = ClashSharpMode.RuleTakeover;
        settings.ActiveProfileId = "retained-startup-reset-profile";
        settings.MixedPort = 12345;
        settings.TransparentProxyEnabled = false;
        settings.ConnectionSamplingEnabled = false;
        settings.ConnectionSamplingIntervalSeconds = 90;
        settings.ConnectionTestUrl = "https://example.invalid/retained-test";
        settings.TrayVisibleFeatureIds = "status,settings";
        settings.MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled;
    }
}
