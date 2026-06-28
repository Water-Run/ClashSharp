/*
 * Master Control ViewModel Tests
 * Verifies master control status and takeover mode behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/MasterControlViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the master control view model.</summary>
public sealed class MasterControlViewModelTests
{
    /// <summary>Verifies construction loads localized static labels and persisted mode state.</summary>
    [Fact]
    public void Constructor_LoadsLocalizedLabelsAndSelectedMode()
    {
        FakeMasterSettings settings = new() { CurrentMode = ClashSharpMode.RuleTakeover };

        MasterControlViewModel viewModel = CreateViewModel(settings: settings);

        Assert.Equal("Master", viewModel.PageTitleText);
        Assert.Equal("Status title", viewModel.StatusControlTitleText);
        Assert.True(viewModel.IsRuleTakeoverModeSelected);
        Assert.False(viewModel.IsDisabledModeSelected);
    }

    /// <summary>Verifies loading probes core version and visible proxy status.</summary>
    [Fact]
    public async Task LoadAsync_WhenServicesSucceed_UpdatesStatusText()
    {
        FakeMasterCore core = new() { VersionText = "Mihomo Meta v1.19.11 windows amd64 with go1.24.4" };
        FakeMasterWindowsProxy proxy = new() { CurrentState = new WindowsProxyState(true, "127.0.0.1:7890") };
        FakeMasterSettings settings = new() { TransparentProxyEnabled = true };
        FakeMasterTrayStatus trayStatus = new() { Snapshot = new TrayStatusSnapshot("HK-01", 82) };
        MasterControlViewModel viewModel = CreateViewModel(core, proxy, settings, trayStatus: trayStatus);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("Core ready: v1.19.11", viewModel.CoreStatusText);
        Assert.Equal("On", viewModel.SystemProxyStatusText);
        Assert.Equal("Standby", viewModel.TransparentProxyStatusText);
        Assert.Equal("Ready", viewModel.BasicStatusText);
        Assert.Equal("HK-01", viewModel.CurrentNodeText);
        Assert.Equal("82 ms", viewModel.LatencySummaryText);
        Assert.DoesNotContain("v1.19.11", viewModel.InfoTiles.Single(tile => tile.Id == "core").Detail);
        Assert.Equal("v1.19.11", viewModel.InfoTiles.Single(tile => tile.Id == "mihomo-version").Value);
    }

    /// <summary>Verifies applying a mode persists the mode, updates statuses, and logs the result.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTakeoverSucceeds_UpdatesStateAndLogs()
    {
        FakeMasterSettings settings = new() { CurrentMode = ClashSharpMode.Disabled };
        FakeMasterTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(ClashSharpMode.FullTakeover, true, true, false, "applied"),
        };
        FakeMasterLog log = new();
        MasterControlViewModel viewModel = CreateViewModel(takeover: takeover, settings: settings, log: log);

        await viewModel.ApplyModeAsync(ClashSharpMode.FullTakeover, CancellationToken.None);

        Assert.Equal(ClashSharpMode.FullTakeover, settings.CurrentMode);
        Assert.True(viewModel.IsFullTakeoverModeSelected);
        Assert.Equal("Running", viewModel.CoreStatusText);
        Assert.Equal("On", viewModel.SystemProxyStatusText);
        Assert.Equal("Off", viewModel.TransparentProxyStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Info" && entry.Message == "applied");
        Assert.Equal("Active", viewModel.BasicStatusText);
    }

    /// <summary>Verifies takeover fallback results are persisted instead of the originally requested mode.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTakeoverReturnsDifferentMode_PersistsResultMode()
    {
        FakeMasterSettings settings = new() { CurrentMode = ClashSharpMode.Disabled };
        FakeMasterTakeover takeover = new()
        {
            Result = new NetworkTakeoverResult(ClashSharpMode.Standby, true, false, false, "fallback"),
        };
        ClashSharpMode? notifiedMode = null;
        MasterControlViewModel viewModel = new(
            new FakeMasterLocalization(),
            new FakeMasterCore(),
            new FakeMasterWindowsProxy(),
            settings,
            takeover,
            new FakeMasterLog(),
            new FakeMasterTrayStatus(),
            modeApplied: mode =>
            {
                notifiedMode = mode;
                return Task.CompletedTask;
            });

        await viewModel.ApplyModeAsync(ClashSharpMode.FullTakeover, CancellationToken.None);

        Assert.Equal(ClashSharpMode.Standby, settings.CurrentMode);
        Assert.Equal(ClashSharpMode.Standby, viewModel.SelectedMode);
        Assert.Equal(ClashSharpMode.Standby, notifiedMode);
    }

    /// <summary>Verifies clicking the already-active mode leaves runtime services untouched.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenModeAlreadySelected_DoesNotApplyOrNotify()
    {
        FakeMasterSettings settings = new() { CurrentMode = ClashSharpMode.Disabled };
        FakeMasterTakeover takeover = new();
        FakeMasterLog log = new();
        int notifiedCount = 0;
        MasterControlViewModel viewModel = new(
            new FakeMasterLocalization(),
            new FakeMasterCore(),
            new FakeMasterWindowsProxy(),
            settings,
            takeover,
            log,
            new FakeMasterTrayStatus(),
            modeApplied: _ =>
            {
                notifiedCount++;
                return Task.CompletedTask;
            });

        await viewModel.ApplyModeAsync(ClashSharpMode.Disabled, CancellationToken.None);

        Assert.Equal(0, takeover.ApplyCount);
        Assert.Equal(0, notifiedCount);
        Assert.Empty(log.Entries);
        Assert.True(viewModel.IsDisabledModeSelected);
    }

    /// <summary>Verifies expected takeover failures move the view model to a faulted state and log an error.</summary>
    [Fact]
    public async Task ApplyModeAsync_WhenTakeoverFails_SetsFaultedStateAndLogs()
    {
        FakeMasterTakeover takeover = new()
        {
            ExceptionToThrow = new InvalidOperationException("missing core"),
        };
        FakeMasterLog log = new();
        MasterControlViewModel viewModel = CreateViewModel(takeover: takeover, log: log);

        await viewModel.ApplyModeAsync(ClashSharpMode.Standby, CancellationToken.None);

        Assert.Equal(ClashSharpMode.Faulted, viewModel.SelectedMode);
        Assert.Equal("Core failed", viewModel.CoreStatusText);
        Assert.Equal("Unavailable", viewModel.BasicStatusText);
        Assert.Contains(log.Entries, entry => entry.Level == "Error" && entry.Detail == "missing core");
    }

    /// <summary>Verifies the redesigned master control exposes information tiles without mixing in editor commands.</summary>
    [Fact]
    public void Constructor_BuildsCompleteInfoTiles()
    {
        FakeMasterSettings settings = new()
        {
            LaunchAtStartupEnabled = true,
            ConnectionSamplingEnabled = true,
            MainlandChinaUrlBlockingEnabled = true,
            ActiveProfileId = "profile-a",
            TransparentProxyEnabled = true,
            MixedPort = 12000,
            ConnectionSamplingIntervalSeconds = 45,
            DisplayLanguage = AppLanguage.English,
            AppThemeMode = AppThemeMode.Dark,
            StartupBehaviorMode = StartupBehaviorMode.StartRuleProxy,
            TriggersEnabled = true,
            TriggerNotificationsEnabled = false,
            CloseBehaviorMode = CloseBehaviorMode.ConfirmExit,
            TrayUseMonochromeInactiveIcon = false,
            TrayVisibleFeatureIds = "status,mode,pages,settings",
            NotificationEnabled = true,
            NotificationLevel = NotificationLevel.More,
            RestoreProxyOnExit = true,
            CheckStaleProxyOnStartup = true,
            StartupConflictCheckEnabled = true,
            ShowStartupGuideOnStartup = false,
            MainlandChinaFeatureMode = MainlandChinaFeatureMode.FlagTextCompletionAndKeywordFilter,
            AppAccentColorMode = AppAccentColorMode.Custom,
            AppAccentColorValue = "#FF112233",
            ConnectionTestProxyUrl1 = "https://google.com",
            ConnectionTestProxyUrl2 = "https://github.com",
            ConnectionTestDirectUrl = "https://baidu.com",
        };

        MasterControlViewModel viewModel = CreateViewModel(settings: settings);

        Assert.True(viewModel.InfoTiles.Count >= 49);
        Assert.Equal(viewModel.InfoTiles.Count, viewModel.InfoTiles.Select(static tile => tile.Id).Distinct(StringComparer.Ordinal).Count());
        Assert.DoesNotContain(viewModel.InfoTiles, tile => tile.Id == "edit-tiles");
        Assert.DoesNotContain(viewModel.InfoTiles, tile => tile.Id == "backup");
        Assert.Equal("core", viewModel.InfoTiles[0].Id);
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "mihomo-version" && tile.Value == string.Empty);
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "transparent-proxy" && tile.IsToggleVisible && tile.IsToggleOn);
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "connection-test-proxy-url-1" && tile.Value == "google.com");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "connection-test-proxy-url-2" && tile.Value == "github.com");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "connection-test-direct-url" && tile.Value == "baidu.com");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "startup-prompt" && tile.TileCommand is not null);
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "startup-conflicts" && tile.TileCommand is not null);
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "startup-launch" && tile.Value == "On");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "connection-sampling" && tile.Value == "On");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "blocked-url" && tile.Value == "On");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "active-profile" && tile.Value == "profile-a");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "port" && tile.Value == "12000");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "app-name" && tile.Value == "Clash#");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "app-version" && tile.Value == "Version 1.0.0.0");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "app-runtime" && tile.Value == ".NET 10 + WinUI 3");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "notification-level" && tile.Value == "More");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "triggers-enabled" && tile.Value == "On");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "trigger-notifications" && tile.Value == "Off");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "tray-visible-features" && tile.Value == "4 features enabled");
        Assert.DoesNotContain(viewModel.InfoTiles, tile => tile.Id == "tray-fade-icon");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "close-behavior" && tile.Value == "Exit with confirmation");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "startup-behavior" && tile.Value == "Start proxy");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "app-theme" && tile.Value == "Dark");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "display-language" && tile.Value == "English");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "sampling-interval" && tile.Value == "45 s");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "app-accent" && tile.Value == "Custom");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "restore-proxy-on-exit" && tile.Value == "On");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "stale-proxy-check" && tile.Value == "On");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "startup-conflict-check" && tile.Value == "On");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "startup-guide" && tile.Value == "Off");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "mainland-feature-mode" && tile.Value == "Keyword filter");
        Assert.All(viewModel.InfoTiles, tile => Assert.False(string.IsNullOrWhiteSpace(tile.Description)));
        Assert.Equal("Search tiles", viewModel.SearchInfoTilesPlaceholderText);
    }

    [Fact]
    public async Task LoadAsync_RefreshesRuntimeInfoTiles()
    {
        FakeMasterRuntime runtime = new()
        {
            Snapshot = new MasterControlRuntimeSnapshot(
                new CoreConfigurationState(@"C:\Data", @"C:\Data\config.yaml", true),
                3,
                2,
                24,
                150,
                5,
                4,
                new LogStorageSummary(@"C:\Data\logs.sqlite", 2048, 9, 11),
                new TrafficStatisticsSummary(1024, 2048, 11, 7, 3, 6, 8, 10),
                new MihomoServiceStatus(true, true, "Service running"),
                new StartupRestoreFallbackStatus(true, "helper.exe --restore")),
        };
        MasterControlViewModel viewModel = CreateViewModel(runtime: runtime);

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "core-config-file" && tile.Value == "Available");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "profile-count" && tile.Value == "3");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "subscription-count" && tile.Value == "2");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "proxy-node-count" && tile.Value == "24");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "rule-count" && tile.Value == "150");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "trigger-count" && tile.Value == "4/5");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "system-log-count" && tile.Value == "9");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "connection-records" && tile.Value == "11");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "traffic-total" && tile.Value == "3 KB");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "traffic-snapshots" && tile.Value == "7");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "node-health-records" && tile.Value == "8");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "mihomo-service" && tile.Value == "Service running");
        Assert.Contains(viewModel.InfoTiles, tile => tile.Id == "startup-restore-fallback" && tile.Value == "Registered");
    }

    /// <summary>Verifies the transparent-proxy tile toggle persists through the settings boundary.</summary>
    [Fact]
    public void ToggleTransparentProxyCommand_UpdatesSettingsAndTile()
    {
        FakeMasterSettings settings = new() { TransparentProxyEnabled = true };
        MasterControlViewModel viewModel = CreateViewModel(settings: settings);
        MasterControlInfoTileViewModel tile = viewModel.InfoTiles.Single(item => item.Id == "transparent-proxy");

        tile.TileCommand?.Execute(null);

        Assert.False(settings.TransparentProxyEnabled);
        Assert.False(tile.IsToggleOn);
        Assert.Equal("Off", viewModel.TransparentProxyStatusText);
    }

    /// <summary>Verifies functional tiles request page-level actions without directly owning dialogs.</summary>
    [Fact]
    public void FunctionalTileCommands_RequestPageActions()
    {
        MasterControlViewModel viewModel = CreateViewModel();
        List<MasterControlTileAction> actions = [];
        viewModel.TileActionRequested += (_, action) => actions.Add(action);

        viewModel.InfoTiles.Single(tile => tile.Id == "startup-prompt").TileCommand?.Execute(null);
        viewModel.InfoTiles.Single(tile => tile.Id == "startup-conflicts").TileCommand?.Execute(null);
        viewModel.InfoTiles.Single(tile => tile.Id == "latency").TileCommand?.Execute(null);

        Assert.Equal(
            [MasterControlTileAction.ShowStartupPrompt, MasterControlTileAction.CheckStartupConflicts, MasterControlTileAction.RunLatencyTest],
            actions);
    }

    /// <summary>Creates a master control view model with fake dependencies.</summary>
    /// <param name="core">Optional fake core service.</param>
    /// <param name="proxy">Optional fake Windows proxy service.</param>
    /// <param name="settings">Optional fake settings store.</param>
    /// <param name="takeover">Optional fake takeover service.</param>
    /// <param name="log">Optional fake log service.</param>
    /// <returns>A configured master control view model.</returns>
    private static MasterControlViewModel CreateViewModel(
        FakeMasterCore? core = null,
        FakeMasterWindowsProxy? proxy = null,
        FakeMasterSettings? settings = null,
        FakeMasterTakeover? takeover = null,
        FakeMasterLog? log = null,
        FakeMasterTrayStatus? trayStatus = null,
        FakeMasterRuntime? runtime = null)
    {
        return new MasterControlViewModel(
            new FakeMasterLocalization(),
            core ?? new FakeMasterCore(),
            proxy ?? new FakeMasterWindowsProxy(),
            settings ?? new FakeMasterSettings(),
            takeover ?? new FakeMasterTakeover(),
            log ?? new FakeMasterLog(),
            trayStatus ?? new FakeMasterTrayStatus(),
            runtime ?? new FakeMasterRuntime());
    }

    /// <summary>Fake localization provider for master-control tests.</summary>
    private sealed class FakeMasterLocalization : IMasterControlLocalization
    {
        /// <summary>Gets a localized string for a key.</summary>
        /// <param name="key">Localization key. Must not be null.</param>
        /// <returns>Localized test string.</returns>
        public string GetString(string key)
        {
            return key switch
            {
                "Nav.MasterControl" => "Master",
                "Page.MasterControl.Description" => "Description",
                "Master.StatusControl.Title" => "Status title",
                "Master.StatusControl.Description" => "Status description",
                "Master.Mode.Disabled.Title" => "Disabled",
                "Master.Mode.Disabled.Description" => "Disabled description",
                "Master.Mode.Standby.Title" => "Standby mode",
                "Master.Mode.Standby.Description" => "Standby description",
                "Master.Mode.RuleTakeover.Title" => "Rule mode",
                "Master.Mode.RuleTakeover.Description" => "Rule description",
                "Master.Mode.FullTakeover.Title" => "Full mode",
                "Master.Mode.FullTakeover.Description" => "Full description",
                "Master.Status.Core" => "Core",
                "Master.Status.SystemProxy" => "System proxy",
                "Master.Status.TransparentProxy" => "Transparent proxy",
                "Master.BasicStatus.Unavailable" => "Unavailable",
                "Master.BasicStatus.Ready" => "Ready",
                "Master.BasicStatus.Active" => "Active",
                "Master.Tile.Core" => "Core",
                "Master.Tile.SystemProxy" => "System proxy",
                "Master.Tile.TransparentProxy" => "Transparent proxy",
                "Master.Tile.Latency" => "Latency",
                "Master.Tile.MihomoVersion" => "Mihomo version",
                "Master.Tile.StartupLaunch" => "Startup launch",
                "Master.Tile.ConnectionSampling" => "Connection sampling",
                "Master.Tile.BlockedUrl" => "Blocked URL",
                "Master.Tile.ActiveProfile" => "Active profile",
                "Master.Tile.Port" => "Port",
                "Master.Tile.ConnectionTest" => "Connection test",
                "Master.Tile.ConnectionTestProxyUrl1" => "Test URL 1",
                "Master.Tile.ConnectionTestProxyUrl2" => "Test URL 2",
                "Master.Tile.ConnectionTestDirectUrl" => "Direct URL",
                "Master.Tile.StartupPrompt" => "Startup prompt",
                "Master.Tile.StartupConflicts" => "Startup conflicts",
                "Master.Tile.Type.Controllable" => "Controllable",
                "Master.Tile.Type.Information" => "Information",
                "Master.Tile.Type.Action" => "Action",
                "Master.Tile.Type.Navigation" => "Navigation",
                "Master.Tile.ExportConfig" => "Export config",
                "Master.Tile.ImportConfig" => "Import config",
                "Master.Tile.AppName" => "App name",
                "Master.Tile.Visible" => "Visible",
                "Master.Tile.Edit" => "Edit tiles",
                "Master.Tile.EditTiles" => "Edit tiles",
                "Master.Tile.SearchPlaceholder" => "Search tiles",
                "Master.Tile.Description.Core" => "Core status description",
                "Master.Tile.Description.SystemProxy" => "System proxy description",
                "Master.Tile.Description.TransparentProxy" => "Transparent proxy description",
                "Master.Tile.Description.Latency" => "Latency description",
                "Master.Tile.Description.MihomoVersion" => "Mihomo version description",
                "Master.Tile.Description.StartupLaunch" => "Startup launch description",
                "Master.Tile.Description.ConnectionSampling" => "Connection sampling description",
                "Master.Tile.Description.BlockedUrl" => "Blocked URL description",
                "Master.Tile.Description.ActiveProfile" => "Active profile description",
                "Master.Tile.Description.Port" => "Port description",
                "Master.Tile.Description.ConnectionTest" => "Connection test description",
                "Master.Tile.Description.ConnectionTestProxyUrl1" => "Proxy URL 1 description",
                "Master.Tile.Description.ConnectionTestProxyUrl2" => "Proxy URL 2 description",
                "Master.Tile.Description.ConnectionTestDirectUrl" => "Direct URL description",
                "Master.Tile.Description.StartupPrompt" => "Startup prompt description",
                "Master.Tile.Description.StartupConflicts" => "Startup conflicts description",
                "Master.Tile.Description.ExportConfig" => "Export config description",
                "Master.Tile.Description.ImportConfig" => "Import config description",
                "Master.Tile.Description.AppName" => "App name description",
                "Master.Tile.Description.AppVersion" => "App version description",
                "Master.Tile.Description.AppRuntime" => "App runtime description",
                "Settings.StartupGuide.ShowNow" => "Show now",
                "Settings.CheckStartupConflicts.Now" => "Check now",
                "Settings.Notification.Enabled.Title" => "Notifications enabled",
                "Settings.Notification.Enabled.Description" => "Notifications enabled description",
                "Settings.Notification.Title" => "Notifications",
                "Settings.Notification.Description" => "Notifications description",
                "Settings.Notification.Default" => "Default",
                "Settings.Notification.CriticalOnly" => "Critical only",
                "Settings.Notification.More" => "More",
                "Settings.Triggers.Enabled.Title" => "Triggers enabled",
                "Settings.Triggers.Enabled.Description" => "Triggers enabled description",
                "Settings.Triggers.Notifications.Title" => "Trigger notifications",
                "Settings.Triggers.Notifications.Description" => "Trigger notifications description",
                "Settings.Section.Triggers" => "Triggers",
                "Settings.Tray.VisibleFeatures.Title" => "Tray features",
                "Settings.Tray.VisibleFeatures.Description" => "Tray features description",
                "Settings.Tray.VisibleFeatures.Summary.Format" => "{0} features enabled",
                "Settings.Tray.MonochromeInactiveIcon.Title" => "Monochrome tray icon",
                "Settings.Tray.MonochromeInactiveIcon.Description" => "Monochrome tray icon description",
                "Settings.CloseBehavior.Title" => "Close behavior",
                "Settings.CloseBehavior.Description" => "Close behavior description",
                "Settings.CloseBehavior.ExitWithoutConfirmation" => "Exit without confirmation",
                "Settings.CloseBehavior.ConfirmExit" => "Exit with confirmation",
                "Settings.CloseBehavior.MinimizeToTray" => "Minimize",
                "Settings.StartupBehavior.Title" => "Startup behavior",
                "Settings.StartupBehavior.Description" => "Startup behavior description",
                "Settings.StartupBehavior.LastSetting" => "Last setting",
                "Settings.StartupBehavior.StartRuleProxy" => "Start proxy",
                "Settings.StartupBehavior.DisableProxy" => "Disable proxy",
                "Settings.AppTheme.Title" => "Theme",
                "Settings.AppTheme.Description" => "Theme description",
                "Settings.AppTheme.FollowSystem" => "Follow system",
                "Settings.AppTheme.Light" => "Light",
                "Settings.AppTheme.Dark" => "Dark",
                "Settings.Language.Title" => "Language",
                "Settings.Language.Description" => "Language description",
                "Settings.Language.AutoDetect" => "Auto detect",
                "Settings.AppAccentColor.Title" => "Accent color",
                "Settings.AppAccentColor.Description" => "Accent color description",
                "Settings.AppAccentColor.FollowSystem" => "Follow system",
                "Settings.AppAccentColor.Custom" => "Custom",
                "Settings.SamplingInterval.Title" => "Sampling interval",
                "Settings.SamplingInterval.Description" => "Sampling interval description",
                "Settings.RestoreProxyOnExit.Title" => "Restore proxy on exit",
                "Settings.RestoreProxyOnExit.Description" => "Restore proxy on exit description",
                "Settings.CheckStaleProxy.Title" => "Stale proxy check",
                "Settings.CheckStaleProxy.Description" => "Stale proxy check description",
                "Settings.StartupConflictCheck.Title" => "Startup conflict check",
                "Settings.StartupConflictCheck.Description" => "Startup conflict check description",
                "Settings.StartupGuide.Title" => "Startup guide",
                "Settings.StartupGuide.Description" => "Startup guide description",
                "Settings.MainlandChinaDisplay.Title" => "Mainland display",
                "Settings.MainlandChinaDisplay.Description" => "Mainland display description",
                "Settings.MainlandChinaFeature.Disabled" => "Disabled",
                "Settings.MainlandChinaFeature.FlagOnly" => "Flag only",
                "Settings.MainlandChinaFeature.FlagAndText" => "Flag and text",
                "Settings.MainlandChinaFeature.KeywordFilter" => "Keyword filter",
                "Settings.MainlandChinaFeature.All" => "All",
                "Settings.StartupRestoreFallback.Title" => "Fallback restore",
                "Settings.StartupRestoreFallback.Description" => "Fallback restore description",
                "Settings.StartupRestoreFallback.Status.Registered" => "Registered",
                "Settings.StartupRestoreFallback.Status.NotRegistered" => "Not registered",
                "Settings.TransparentProxy.Service.Title" => "Mihomo service",
                "Settings.TransparentProxy.Service.Description" => "Mihomo service description",
                "Settings.ProxyInformation.Description" => "Proxy information description",
                "Settings.ProxyInformation.CoreBinary.Missing" => "Missing",
                "Tray.Menu.Mode" => "Mode",
                "Tray.Status.Node.Format" => "Node: {0}",
                "Nav.Profiles" => "Profiles",
                "Page.Profiles.Description" => "Profiles description",
                "StartupPrompt.Check.Subscription.Title" => "Subscriptions",
                "Nav.ProxyNodes" => "Nodes",
                "Page.ProxyNodes.Description" => "Nodes description",
                "Nav.Rules" => "Rules",
                "Page.Rules.Description" => "Rules description",
                "Page.Triggers.Description" => "Triggers description",
                "Statistics.LogsShortcut.Title" => "System logs",
                "Statistics.LogsShortcut.Description" => "System logs description",
                "Nav.Connections" => "Connections",
                "Page.Connections.Description" => "Connections description",
                "Statistics.Total.Title" => "Total",
                "Page.Statistics.Description" => "Statistics description",
                "Statistics.TotalTraffic.Format" => "Upload {0} / download {1}",
                "Statistics.ByDate.Title" => "By date",
                "Statistics.Node.Title" => "Nodes",
                "ProfileCatalog.Status.Available" => "Available",
                "About.App.Description" => "Clash# app",
                "About.Version.Title" => "Version",
                "About.Version.Value.Format" => "Version {0}",
                "About.Runtime.Title" => "Runtime",
                "About.Runtime.Value" => ".NET 10 + WinUI 3",
                "Master.Log.ApplyModeFailed" => "Mode failed",
                "Master.Status.Seconds.Format" => "{0} s",
                "Master.Status.CurrentNodeUnavailable" => "No node",
                "Master.Status.LatencyUnavailable" => "Not tested",
                "Master.Status.Latency.Format" => "{0} ms",
                "Master.Status.StartupLaunchOn" => "On",
                "Master.Status.StartupLaunchOff" => "Off",
                "Master.Status.CoreReady.Format" => "Core ready: {0}",
                "Master.Status.CoreUnavailable" => "Core unavailable",
                "Master.Status.Running" => "Running",
                "Master.Status.NotRunning" => "Not running",
                "Master.Status.On" => "On",
                "Master.Status.Off" => "Off",
                "Master.Status.Fallback" => "Fallback",
                "Master.Status.CoreStartFailed" => "Core failed",
                "Master.Status.Unavailable" => "Unavailable",
                "Master.Status.Standby" => "Standby",
                _ => key,
            };
        }
    }

    /// <summary>Fake core service for master-control tests.</summary>
    private sealed class FakeMasterCore : IMasterControlCore
    {
        /// <summary>Gets or sets the version text returned by the fake core.</summary>
        /// <value>Version text returned by <see cref="GetVersionTextAsync"/>.</value>
        public string VersionText { get; set; } = "mihomo";

        /// <summary>Gets or sets an exception to throw during version probing.</summary>
        /// <value>Exception thrown when non-null.</value>
        public Exception? ExceptionToThrow { get; set; }

        /// <summary>Gets fake core version text.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured version text.</returns>
        public Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
        {
            return ExceptionToThrow is null
                ? Task.FromResult(VersionText)
                : Task.FromException<string>(ExceptionToThrow);
        }
    }

    /// <summary>Fake Windows proxy service for master-control tests.</summary>
    private sealed class FakeMasterWindowsProxy : IMasterControlWindowsProxy
    {
        /// <summary>Gets or sets the proxy state returned by the fake service.</summary>
        /// <value>Current fake proxy state.</value>
        public WindowsProxyState CurrentState { get; set; } = new(false, string.Empty);

        /// <summary>Gets current fake proxy state.</summary>
        /// <returns>Configured fake proxy state.</returns>
        public WindowsProxyState GetCurrentState()
        {
            return CurrentState;
        }
    }

    /// <summary>Fake settings store for master-control tests.</summary>
    private sealed class FakeMasterSettings : IMasterControlSettings
    {
        /// <summary>Gets or sets the current master mode.</summary>
        /// <value>Current fake mode.</value>
        public ClashSharpMode CurrentMode { get; set; } = ClashSharpMode.Disabled;

        /// <summary>Gets or sets whether transparent proxy is enabled.</summary>
        /// <value>True when transparent proxy is enabled.</value>
        public bool TransparentProxyEnabled { get; set; }

        public bool LaunchAtStartupEnabled { get; set; }

        public bool ConnectionSamplingEnabled { get; set; } = true;

        public bool MainlandChinaUrlBlockingEnabled { get; set; }

        public string ActiveProfileId { get; set; } = "direct";

        public int MixedPort { get; set; } = 10000;

        public string ConnectionTestProxyUrl1 { get; set; } = "https://www.google.com";

        public string ConnectionTestProxyUrl2 { get; set; } = "https://github.com";

        public string ConnectionTestDirectUrl { get; set; } = "https://www.baidu.com";

        public AppLanguage DisplayLanguage { get; set; } = AppLanguage.AutoDetect;

        public AppThemeMode AppThemeMode { get; set; } = AppThemeMode.FollowSystem;

        public int ConnectionSamplingIntervalSeconds { get; set; } = 30;

        public StartupBehaviorMode StartupBehaviorMode { get; set; } = StartupBehaviorMode.LastSetting;

        public bool TriggersEnabled { get; set; } = true;

        public bool TriggerNotificationsEnabled { get; set; } = true;

        public CloseBehaviorMode CloseBehaviorMode { get; set; } = CloseBehaviorMode.MinimizeToTray;

        public bool TrayUseMonochromeInactiveIcon { get; set; }

        public string TrayVisibleFeatureIds { get; set; } = "status,mode,pages,transparent-proxy,settings,safe-exit";

        public bool NotificationEnabled { get; set; } = true;

        public NotificationLevel NotificationLevel { get; set; } = NotificationLevel.Default;

        public bool RestoreProxyOnExit { get; set; } = true;

        public bool CheckStaleProxyOnStartup { get; set; } = true;

        public bool StartupConflictCheckEnabled { get; set; } = true;

        public bool ShowStartupGuideOnStartup { get; set; } = true;

        public MainlandChinaFeatureMode MainlandChinaFeatureMode { get; set; } = MainlandChinaFeatureMode.FlagReplacementAndTextCompletion;

        public AppAccentColorMode AppAccentColorMode { get; set; } = AppAccentColorMode.FollowSystem;

        public string AppAccentColorValue { get; set; } = "#FF0078D4";
    }

    /// <summary>Fake takeover service for master-control tests.</summary>
    private sealed class FakeMasterTakeover : IMasterControlTakeover
    {
        /// <summary>Gets or sets the result returned by the fake takeover service.</summary>
        /// <value>Configured fake takeover result.</value>
        public NetworkTakeoverResult Result { get; set; } = new(ClashSharpMode.Disabled, false, false, false, "disabled");

        /// <summary>Gets or sets an exception to throw when applying a mode.</summary>
        /// <value>Exception thrown when non-null.</value>
        public Exception? ExceptionToThrow { get; set; }

        /// <summary>Applies a fake master mode.</summary>
        /// <param name="mode">Mode requested by the view model.</param>
        /// <returns>Configured takeover result using the requested mode when the result has default disabled mode.</returns>
        public int ApplyCount { get; private set; }

        public NetworkTakeoverResult ApplyMode(ClashSharpMode mode)
        {
            ApplyCount++;
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Result.Mode == ClashSharpMode.Disabled && mode != ClashSharpMode.Disabled
                ? Result with { Mode = mode }
                : Result;
        }
    }

    /// <summary>Fake log service for master-control tests.</summary>
    private sealed class FakeMasterLog : IMasterControlLog
    {
        /// <summary>Gets captured log entries.</summary>
        /// <value>Mutable list of captured entries.</value>
        public List<LogEntry> Entries { get; } = [];

        /// <summary>Captures one fake log entry.</summary>
        /// <param name="level">Log level. Must not be null.</param>
        /// <param name="category">Log category. Must not be null.</param>
        /// <param name="message">Log message. Must not be null.</param>
        /// <param name="detail">Optional detail text.</param>
        public void Append(string level, string category, string message, string? detail)
        {
            Entries.Add(new LogEntry(level, category, message, detail));
        }
    }

    /// <summary>Captured log entry.</summary>
    /// <param name="Level">Log level.</param>
    /// <param name="Category">Log category.</param>
    /// <param name="Message">Log message.</param>
    /// <param name="Detail">Optional detail text.</param>
    private sealed record LogEntry(string Level, string Category, string Message, string? Detail);

    /// <summary>Fake tray-status provider for master-control tests.</summary>
    private sealed class FakeMasterTrayStatus : IMasterControlTrayStatus
    {
        public TrayStatusSnapshot Snapshot { get; set; } = TrayStatusSnapshot.Unavailable;

        public TrayStatusSnapshot GetSnapshot()
        {
            return Snapshot;
        }
    }

    private sealed class FakeMasterRuntime : IMasterControlRuntime
    {
        public MasterControlRuntimeSnapshot Snapshot { get; set; } = MasterControlRuntimeSnapshot.Unavailable;

        public MasterControlRuntimeSnapshot GetSnapshot()
        {
            return Snapshot;
        }
    }
}
