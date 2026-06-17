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
            TransparentProxyEnabled = false,
            FallbackToSystemProxyWhenTunFails = false,
            MixedPort = 10990,
            ConnectionSamplingEnabled = false,
            ConnectionSamplingIntervalSeconds = 45,
            CheckStaleProxyOnStartup = false,
            RestoreProxyOnExit = false,
            ProxyRecoveryMode = ProxyRecoveryMode.EnableProxy,
            MainlandChinaDisplayEnabled = false,
        };

        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        viewModel.Load();

        Assert.Equal(AppLanguage.French, viewModel.DisplayLanguage);
        Assert.Equal((int)AppLanguage.French, viewModel.DisplayLanguageIndex);
        Assert.False(viewModel.TransparentProxyEnabled);
        Assert.False(viewModel.FallbackToSystemProxyWhenTunFails);
        Assert.Equal(10990, viewModel.MixedPort);
        Assert.False(viewModel.ConnectionSamplingEnabled);
        Assert.Equal(45, viewModel.ConnectionSamplingIntervalSeconds);
        Assert.False(viewModel.CheckStaleProxyOnStartup);
        Assert.False(viewModel.RestoreProxyOnExit);
        Assert.Equal(ProxyRecoveryMode.EnableProxy, viewModel.ProxyRecoveryMode);
        Assert.Equal((int)ProxyRecoveryMode.EnableProxy, viewModel.ProxyRecoveryModeIndex);
        Assert.False(viewModel.MainlandChinaDisplayEnabled);
    }

    /// <summary>Verifies language selection persists and notifies the shell language controller.</summary>
    [Fact]
    public void SetDisplayLanguageIndex_ValidIndex_PersistsAndNotifiesLanguageChange()
    {
        FakeSettingsStore store = new();
        AppLanguage? notifiedLanguage = null;
        SettingsViewModel viewModel = new(store, language => notifiedLanguage = language, () => { });

        bool changed = viewModel.SetDisplayLanguageIndex((int)AppLanguage.German);

        Assert.True(changed);
        Assert.Equal(AppLanguage.German, store.DisplayLanguage);
        Assert.Equal(AppLanguage.German, viewModel.DisplayLanguage);
        Assert.Equal(AppLanguage.German, notifiedLanguage);
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
    [InlineData(double.NaN, 7890, false)]
    [InlineData(0d, 7890, false)]
    [InlineData(65536d, 7890, false)]
    [InlineData(7891.49d, 7891, true)]
    [InlineData(7891.50d, 7892, true)]
    public void SetMixedPort_ValidatesAndRoundsInput(double input, int expectedPort, bool expectedResult)
    {
        FakeSettingsStore store = new() { MixedPort = 7890 };
        SettingsViewModel viewModel = new(store, _ => { }, () => { });

        bool changed = viewModel.SetMixedPort(input);

        Assert.Equal(expectedResult, changed);
        Assert.Equal(expectedPort, store.MixedPort);
        Assert.Equal(expectedPort, viewModel.MixedPort);
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

    private sealed class FakeSettingsStore : ISettingsStore
    {
        public AppLanguage DisplayLanguage { get; set; } = AppLanguage.SimplifiedChinese;

        public bool TransparentProxyEnabled { get; set; } = true;

        public bool FallbackToSystemProxyWhenTunFails { get; set; } = true;

        public int MixedPort { get; set; } = 7890;

        public bool ConnectionSamplingEnabled { get; set; } = true;

        public int ConnectionSamplingIntervalSeconds { get; set; } = 30;

        public bool CheckStaleProxyOnStartup { get; set; } = true;

        public bool RestoreProxyOnExit { get; set; } = true;

        public ProxyRecoveryMode ProxyRecoveryMode { get; set; } = ProxyRecoveryMode.DisableProxy;

        public bool MainlandChinaDisplayEnabled { get; set; } = true;
    }
}
