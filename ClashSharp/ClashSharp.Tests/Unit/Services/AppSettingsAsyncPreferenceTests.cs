using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Verifies the production asynchronous preference boundary with isolated test settings.</summary>
public sealed class AppSettingsAsyncPreferenceTests : IDisposable
{
    private readonly MutationAdmissionBarrier _barrier = new();
    private readonly AppSettingsService _settings = AppSettingsService.Instance;

    public AppSettingsAsyncPreferenceTests()
    {
        _settings.ConfigureMutationAdmission(_barrier);
        _settings.ResetAllSettings();
    }

    [Fact]
    public async Task ApplyChanges_ObserverFailureLeavesAnIndependentlyReadableCompleteCommit()
    {
        IOException failure = new("observer unavailable");
        void OnChanged(object? sender, AppSettingChangedEventArgs change) => throw failure;
        _settings.SettingChanged += OnChanged;
        try
        {
            Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => _settings.ApplyChangesAsync(
                [Change(SettingsRegistry.Keys.AppAccentColorMode, AppAccentColorMode.Custom),
                 Change(SettingsRegistry.Keys.AppAccentColorValue, "#2d7d9a")], CancellationToken.None)));

            IReadOnlyList<SettingValueChange> committed = _settings.ReadPreferenceChanges(
                [SettingsRegistry.Keys.AppAccentColorMode, SettingsRegistry.Keys.AppAccentColorValue]);
            Assert.Equal(AppAccentColorMode.Custom, committed[0].Value.Get<AppAccentColorMode>());
            Assert.Equal("#FF2D7D9A", committed[1].Value.Get<string>());
            Assert.Equal(MutationAdmissionState.Open, _barrier.State);
        }
        finally { _settings.SettingChanged -= OnChanged; }
    }

    [Fact]
    public async Task ApplyChanges_HoldsAdmissionUntilTheCompleteBatchHasBeenObserved()
    {
        List<(AppAccentColorMode, string)> observations = [];
        ValueTask<MutationAdmissionLease> pendingExclusive = default;
        void OnChanged(object? sender, AppSettingChangedEventArgs change)
        {
            observations.Add((_settings.AppAccentColorMode, _settings.AppAccentColorValue));
            if (observations.Count == 1)
            {
                pendingExclusive = _barrier.CloseAndDrainAsync(MutationAdmissionClosure.Destructive, CancellationToken.None);
            }
            Assert.False(pendingExclusive.IsCompleted);
        }
        _settings.SettingChanged += OnChanged;
        try
        {
            await _settings.ApplyChangesAsync(
                [Change(SettingsRegistry.Keys.AppAccentColorMode, AppAccentColorMode.Custom),
                 Change(SettingsRegistry.Keys.AppAccentColorValue, "#2d7d9a")], CancellationToken.None);

            await using MutationAdmissionLease exclusive = await pendingExclusive;
            Assert.Equal(2, observations.Count);
            Assert.All(observations, observed => Assert.Equal((AppAccentColorMode.Custom, "#FF2D7D9A"), observed));
        }
        finally { _settings.SettingChanged -= OnChanged; }
    }

    [Fact]
    public async Task ApplyChanges_RejectsTheEntireBatchWhenAnyValueHasTheWrongType()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _settings.ApplyChangesAsync(
            [Change(SettingsRegistry.Keys.NotificationEnabled, false),
             new(SettingsRegistry.Keys.NotificationLevel, Change(SettingsRegistry.Keys.MixedPort, 12345).Value)], CancellationToken.None));

        Assert.True(_settings.NotificationEnabled);
        Assert.Equal(NotificationLevel.Default, _settings.NotificationLevel);
    }

    [Fact]
    public async Task ApplyChanges_RejectsDuplicatesAndAliasesBeforeWritingAnything()
    {
        SettingValueChange disabled = Change(SettingsRegistry.Keys.NotificationEnabled, false);
        await Assert.ThrowsAsync<ArgumentException>(() => _settings.ApplyChangesAsync([disabled, disabled], CancellationToken.None));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => _settings.ApplyChangesAsync(
            [disabled, new(SettingsRegistry.Keys.MainlandChinaDisplayEnabled, disabled.Value)], CancellationToken.None));

        Assert.True(_settings.NotificationEnabled);
    }

    [Fact]
    public async Task ApplyChanges_ExclusiveAdmissionRejectsTheWholeBatch()
    {
        await using MutationAdmissionLease exclusive = await _barrier.CloseAndDrainAsync(
            MutationAdmissionClosure.Destructive, CancellationToken.None);

        await Assert.ThrowsAsync<MutationAdmissionRejectedException>(() => _settings.ApplyChangesAsync(
            [Change(SettingsRegistry.Keys.NotificationEnabled, false),
             Change(SettingsRegistry.Keys.NotificationLevel, NotificationLevel.CriticalOnly)], CancellationToken.None));

        Assert.True(_settings.NotificationEnabled);
        Assert.Equal(NotificationLevel.Default, _settings.NotificationLevel);
    }

    [Fact]
    public async Task ApplyChanges_CancellationAfterPublicationDoesNotAbandonTheAcceptedBatch()
    {
        using CancellationTokenSource cancellation = new();
        int observations = 0;
        void OnChanged(object? sender, AppSettingChangedEventArgs change)
        {
            cancellation.Cancel();
            observations++;
            Assert.False(_settings.NotificationEnabled);
            Assert.Equal(NotificationLevel.CriticalOnly, _settings.NotificationLevel);
        }
        _settings.SettingChanged += OnChanged;
        try
        {
            await _settings.ApplyChangesAsync(
                [Change(SettingsRegistry.Keys.NotificationEnabled, false),
                 Change(SettingsRegistry.Keys.NotificationLevel, NotificationLevel.CriticalOnly)], cancellation.Token);
            Assert.Equal(2, observations);
        }
        finally { _settings.SettingChanged -= OnChanged; }
    }

    [Fact]
    public async Task ApplyChanges_AlreadyCancelledRequestHasNoEffect()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _settings.ApplyChangesAsync(
            [Change(SettingsRegistry.Keys.NotificationEnabled, false)], cancellation.Token));

        Assert.True(_settings.NotificationEnabled);
    }

    [Fact]
    public async Task ApplyChanges_RegionalAliasAndCanonicalValueStayConsistentUntilMigration()
    {
        await _settings.ApplyChangesAsync(
            [Change(SettingsRegistry.Keys.MainlandChinaFeatureMode, MainlandChinaFeatureMode.Disabled)], CancellationToken.None);

        Assert.Equal(MainlandChinaFeatureMode.Disabled, _settings.MainlandChinaFeatureMode);
        Assert.False(_settings.MainlandChinaDisplayEnabled);
        await _settings.ResetPreferenceGroupAsync(SettingsResetScope.MainlandChina, CancellationToken.None);
        Assert.Equal(MainlandChinaFeatureMode.FlagReplacementAndTextCompletion, _settings.MainlandChinaFeatureMode);
        Assert.True(_settings.MainlandChinaDisplayEnabled);
    }

    [Theory]
    [InlineData(SettingsResetScope.Basic)]
    [InlineData(SettingsResetScope.Notifications)]
    [InlineData(SettingsResetScope.Triggers)]
    [InlineData(SettingsResetScope.Tray)]
    [InlineData(SettingsResetScope.WindowsNative)]
    [InlineData(SettingsResetScope.MainlandChina)]
    public async Task ResetPreferenceGroup_ObservesCancellationBeforeAnyReset(SettingsResetScope scope)
    {
        _settings.AppThemeMode = AppThemeMode.Dark;
        _settings.NotificationEnabled = false;
        _settings.TriggersEnabled = false;
        _settings.TrayVisibleFeatureIds = "status";
        _settings.RestoreProxyOnExit = false;
        _settings.MainlandChinaFeatureMode = MainlandChinaFeatureMode.Disabled;
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _settings.ResetPreferenceGroupAsync(scope, cancellation.Token));

        Assert.Equal(AppThemeMode.Dark, _settings.AppThemeMode);
        Assert.False(_settings.NotificationEnabled);
        Assert.False(_settings.TriggersEnabled);
        Assert.Equal("status", _settings.TrayVisibleFeatureIds);
        Assert.False(_settings.RestoreProxyOnExit);
        Assert.Equal(MainlandChinaFeatureMode.Disabled, _settings.MainlandChinaFeatureMode);
    }

    public void Dispose()
    {
        _settings.ConfigureMutationAdmission(new MutationAdmissionBarrier());
        _settings.ResetAllSettings();
    }

    private static SettingValueChange Change<T>(SettingKey key, T value) where T : notnull =>
        new(key, SettingsRegistry.Default.Get(key.Value).NormalizeValue(value).Value!);
}
