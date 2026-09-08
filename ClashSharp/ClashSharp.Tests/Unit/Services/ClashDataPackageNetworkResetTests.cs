using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

public sealed partial class ClashDataPackageServiceTests
{
    [Theory]
    [InlineData(SettingsResetScope.Proxy, false, false)]
    [InlineData(SettingsResetScope.Proxy, true, false)]
    [InlineData(SettingsResetScope.Proxy, false, true)]
    [InlineData(SettingsResetScope.Proxy, true, true)]
    [InlineData(SettingsResetScope.TransparentProxy, false, false)]
    [InlineData(SettingsResetScope.TransparentProxy, true, false)]
    [InlineData(SettingsResetScope.TransparentProxy, false, true)]
    [InlineData(SettingsResetScope.TransparentProxy, true, true)]
    public async Task NetworkReset_RetainsAndPublishesTheExactGroupWithTheSupportedTunDefault(
        SettingsResetScope scope,
        bool supportedTun,
        bool rollback)
    {
        using TemporaryDirectory directory = new();
        MutationAdmissionBarrier barrier = new();
        AppSettingsService settings = AppSettingsService.Instance;
        settings.ConfigureMutationAdmission(barrier);
        settings.ResetAllSettings();
        ClashDataPackageSettingsAdapter adapter = new(settings);
        Dictionary<string, object?> defaults = CapturePackageSettingValues(adapter);
        SeedNetworkResetSettings(settings, !supportedTun);
        Dictionary<string, object?> before = CapturePackageSettingValues(adapter);
        Dictionary<string, object?> expected = NetworkDefaultsFrom(scope, before, defaults, supportedTun);
        List<Dictionary<string, object?>> observations = [];
        void Changed(object? sender, AppSettingChangedEventArgs change) => observations.Add(CapturePackageSettingValues(adapter));
        settings.SettingChanged += Changed;
        try
        {
            await using MutationAdmissionLease lease = await barrier.CloseAndDrainAsync(
                MutationAdmissionClosure.Destructive, CancellationToken.None);
            await using DataPackageTransactionReceipt receipt = new ClashDataPackageService(adapter, directory.Path)
                .BeginResetNetworkSettingsAdmitted(lease, scope, supportedTun);

            Assert.Equal(expected.OrderBy(pair => pair.Key), CapturePackageSettingValues(adapter).OrderBy(pair => pair.Key));
            Assert.Equal(scope == SettingsResetScope.Proxy ? 8 : 1, observations.Count);
            Assert.All(observations, state => Assert.Equal(expected.OrderBy(pair => pair.Key), state.OrderBy(pair => pair.Key)));
            Assert.Throws<MutationAdmissionRejectedException>(() => settings.LaunchAtStartupEnabled = false);
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
    [InlineData(SettingsResetScope.Proxy, (int)DataPackageTransactionCheckpoint.ManifestPersisted, false)]
    [InlineData(SettingsResetScope.Proxy, (int)DataPackageTransactionCheckpoint.ResetMutationCompleted, false)]
    [InlineData(SettingsResetScope.Proxy, (int)DataPackageTransactionCheckpoint.ResetSettingsApplied, true)]
    [InlineData(SettingsResetScope.TransparentProxy, (int)DataPackageTransactionCheckpoint.ManifestPersisted, false)]
    [InlineData(SettingsResetScope.TransparentProxy, (int)DataPackageTransactionCheckpoint.ResetMutationCompleted, false)]
    [InlineData(SettingsResetScope.TransparentProxy, (int)DataPackageTransactionCheckpoint.ResetSettingsApplied, true)]
    public async Task NetworkReset_NewServiceReplaysTheRecordedTunDecisionAndPreservesOtherGroups(
        SettingsResetScope scope,
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
        SeedNetworkResetSettings(settings, true);
        Dictionary<string, object?> before = CapturePackageSettingValues(adapter);
        Dictionary<string, object?> expected = forward ? NetworkDefaultsFrom(scope, before, defaults, false) : before;
        try
        {
            await using MutationAdmissionLease lease = await barrier.CloseAndDrainAsync(
                MutationAdmissionClosure.Destructive, CancellationToken.None);
            ClashDataPackageService interrupted = new(adapter, directory.Path, checkpoint =>
            {
                if ((int)checkpoint == crashAt)
                {
                    throw new DataPackageSimulatedCrashException("Injected network reset interruption.");
                }
            });
            Assert.Throws<DataPackageSimulatedCrashException>(
                () => interrupted.BeginResetNetworkSettingsAdmitted(lease, scope, transparentProxyEnabled: false));

            // The recovered service receives neither the old scope nor the observed service capability.
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

    [Theory]
    [InlineData(SettingsResetScope.None)]
    [InlineData(SettingsResetScope.All)]
    [InlineData(SettingsResetScope.Startup)]
    [InlineData(SettingsResetScope.Basic)]
    [InlineData(SettingsResetScope.Proxy | SettingsResetScope.TransparentProxy)]
    public async Task NetworkReset_RejectsUnrelatedAndCombinedScopesBeforeCreatingAJournal(SettingsResetScope scope)
    {
        using TemporaryDirectory directory = new();
        MutationAdmissionBarrier barrier = new();
        FakeClashDataPackageSettings settings = new() { Admission = barrier, MixedPort = 12345 };
        ClashDataPackageService service = new(settings, directory.Path);
        await using MutationAdmissionLease lease = await barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive, CancellationToken.None);

        Assert.Throws<ArgumentOutOfRangeException>(() => service.BeginResetNetworkSettingsAdmitted(lease, scope, false));

        Assert.Equal(12345, settings.MixedPort);
        Assert.Equal(0, settings.AdmittedWriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(SettingsResetScope.Proxy)]
    [InlineData(SettingsResetScope.TransparentProxy)]
    public void NetworkReset_RejectsOrdinaryAdmissionBeforeCreatingAJournal(SettingsResetScope scope)
    {
        using TemporaryDirectory directory = new();
        MutationAdmissionBarrier barrier = new();
        FakeClashDataPackageSettings settings = new() { Admission = barrier, MixedPort = 12345 };
        ClashDataPackageService service = new(settings, directory.Path);
        using MutationAdmissionLease ordinary = barrier.AcquireOrdinary();

        Assert.Throws<ArgumentException>(() => service.BeginResetNetworkSettingsAdmitted(ordinary, scope, false));

        Assert.Equal(12345, settings.MixedPort);
        Assert.Equal(0, settings.AdmittedWriteCalls);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    private static Dictionary<string, object?> NetworkDefaultsFrom(
        SettingsResetScope scope,
        Dictionary<string, object?> before,
        Dictionary<string, object?> defaults,
        bool supportedTun)
    {
        string[] keys = scope == SettingsResetScope.Proxy
            ? [nameof(IClashDataPackageSettings.TransparentProxyEnabled), nameof(IClashDataPackageSettings.MixedPort),
                nameof(IClashDataPackageSettings.ConnectionSamplingEnabled), nameof(IClashDataPackageSettings.ConnectionSamplingIntervalSeconds),
                nameof(IClashDataPackageSettings.ConnectionTestUrl), nameof(IClashDataPackageSettings.ConnectionTestProxyUrl1),
                nameof(IClashDataPackageSettings.ConnectionTestProxyUrl2), nameof(IClashDataPackageSettings.ConnectionTestDirectUrl)]
            : [nameof(IClashDataPackageSettings.TransparentProxyEnabled)];
        Assert.Equal(keys.Order(StringComparer.Ordinal), SettingsRegistry.Default.GetResetDefinitions(scope)
            .Select(definition => definition.Key.Value).Order(StringComparer.Ordinal));
        Dictionary<string, object?> expected = new(before, StringComparer.Ordinal);
        foreach (string key in keys)
        {
            expected[key] = defaults[key];
        }

        expected[nameof(IClashDataPackageSettings.TransparentProxyEnabled)] = supportedTun;
        return expected;
    }

    private static void SeedNetworkResetSettings(AppSettingsService settings, bool tunEnabled)
    {
        SeedStartupResetSettings(settings);
        settings.TransparentProxyEnabled = tunEnabled;
        settings.ConnectionTestProxyUrl1 = "https://example.invalid/old-proxy-one";
        settings.ConnectionTestProxyUrl2 = "https://example.invalid/old-proxy-two";
        settings.ConnectionTestDirectUrl = "https://example.invalid/old-direct";
    }
}
