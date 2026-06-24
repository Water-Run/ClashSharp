/*
 * Display Page ViewModel Tests
 * Verifies read-oriented page view models preserve existing display behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/DisplayPageViewModelTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for rules, statistics, and about page view models.</summary>
public sealed class DisplayPageViewModelTests
{
    /// <summary>Verifies rules view model loads labels and rule rows.</summary>
    [Fact]
    public void RulesViewModel_Constructor_LoadsLabelsAndRules()
    {
        FakeDisplayLocalization localization = new();
        FakeRuleCatalog rules = new();

        RulesViewModel viewModel = new(localization, rules);

        Assert.Equal("Rules", viewModel.PageTitleText);
        Assert.Equal("Rules description", viewModel.DescriptionText);
        Assert.Equal(rules.Rules, viewModel.Rules);
    }

    /// <summary>Verifies statistics view model formats summary and loads row collections.</summary>
    [Fact]
    public void StatisticsViewModel_Refresh_LoadsSummaryAndRows()
    {
        FakeDisplayLocalization localization = new();
        FakeStatisticsStore statistics = new();
        FakeStatisticsProfiles profiles = new();

        StatisticsViewModel viewModel = new(localization, statistics, profiles, () => { });

        Assert.Equal("1.0 KB / 2.0 KB", viewModel.TotalTrafficText);
        Assert.Equal("3 connections", viewModel.ConnectionCountText);
        Assert.Equal("1 profiles", viewModel.ProfileStatisticText);
        Assert.Equal("5 snapshots", viewModel.SnapshotStatisticText);
        Assert.Equal("2 nodes / 4 health", viewModel.NodeStatisticText);
        Assert.Equal("6 rules", viewModel.RuleStatisticText);
        Assert.Equal("Active profile", viewModel.ProfileTrafficRows.Single().Label);
        Assert.Equal(statistics.DailyRows, viewModel.DailyTrafficRows);
        Assert.Equal(statistics.NodeRows, viewModel.NodeTrafficRows);
    }

    /// <summary>Verifies statistics view model invokes the supplied logs navigation action.</summary>
    [Fact]
    public void StatisticsViewModel_OpenLogsCommand_InvokesNavigation()
    {
        bool navigated = false;
        StatisticsViewModel viewModel = new(new FakeDisplayLocalization(), new FakeStatisticsStore(), new FakeStatisticsProfiles(), () => navigated = true);

        viewModel.OpenLogsCommand.Execute(null);

        Assert.True(navigated);
    }

    /// <summary>Verifies about view model loads text and mihomo status.</summary>
    [Fact]
    public async Task AboutViewModel_LoadAsync_WhenCoreAvailable_FormatsVersionStatus()
    {
        FakeAboutCore core = new() { VersionText = "Mihomo Meta v1.19.11 windows amd64 with go1.24.4" };
        AboutViewModel viewModel = new(new FakeDisplayLocalization(), core, new FakeUriLauncher());

        await viewModel.LoadAsync(CancellationToken.None);

        Assert.Equal("About", viewModel.PageTitleText);
        Assert.Equal("mihomo available: v1.19.11", viewModel.MihomoStatusText);
    }

    /// <summary>Verifies core version output is reduced to the stable semantic version token when possible.</summary>
    [Theory]
    [InlineData("Mihomo Meta v1.19.11 windows amd64 with go1.24.4", "v1.19.11")]
    [InlineData("mihomo 1.18.10", "1.18.10")]
    [InlineData("custom core build", "custom core build")]
    public void CoreVersionDisplayFormatter_Format_ExtractsStableVersionText(string rawText, string expectedText)
    {
        string formattedText = CoreVersionDisplayFormatter.Format(rawText);

        Assert.Equal(expectedText, formattedText);
    }

    /// <summary>Verifies about view model launch commands call the URL launcher.</summary>
    [Fact]
    public async Task AboutViewModel_LinkCommands_LaunchExpectedUris()
    {
        FakeUriLauncher launcher = new();
        AboutViewModel viewModel = new(new FakeDisplayLocalization(), new FakeAboutCore(), launcher);

        await viewModel.OpenGitHubCommand.ExecuteAsync(null);
        await viewModel.OpenMihomoCommand.ExecuteAsync(null);

        Assert.Contains("https://github.com/Water-Run/ClashSharp", launcher.LaunchedUris.Select(uri => uri.ToString()));
        Assert.Contains("https://github.com/MetaCubeX/mihomo", launcher.LaunchedUris.Select(uri => uri.ToString()));
    }

    /// <summary>Fake localization provider shared by display page tests.</summary>
    private sealed class FakeDisplayLocalization : IDisplayPageLocalization
    {
        /// <summary>Gets a localized string for a key.</summary>
        /// <param name="key">Localization key. Must not be null.</param>
        /// <returns>Localized test string.</returns>
        public string GetString(string key)
        {
            return key switch
            {
                "Nav.Rules" => "Rules",
                "Page.Rules.Description" => "Rules description",
                "Nav.Statistics" => "Statistics",
                "Page.Statistics.Description" => "Statistics description",
                "Statistics.Total.Title" => "Total",
                "Statistics.Profile.Title" => "Profiles",
                "Statistics.Node.Title" => "Nodes",
                "Statistics.ByProfile.Title" => "By profile",
                "Statistics.ByDate.Title" => "By date",
                "Statistics.ByNode.Title" => "By node",
                "Statistics.LogsShortcut.Title" => "Logs",
                "Statistics.LogsShortcut.Description" => "Open logs",
                "Statistics.OpenLogs" => "Open",
                "Statistics.TotalTraffic.Format" => "{0} / {1}",
                "Statistics.ConnectionCount.Format" => "{0} connections",
                "Statistics.ProfileCount.Format" => "{0} profiles",
                "Statistics.SnapshotCount.Format" => "{0} snapshots",
                "Statistics.NodeCount.Format" => "{0} nodes / {1} health",
                "Statistics.RuleCount.Format" => "{0} rules",
                "Nav.About" => "About",
                "Page.About.Description" => "About description",
                "About.App.Description" => "App description",
                "About.Author.Title" => "Author",
                "About.Author.Value" => "WaterRun",
                "About.OpenSource.Title" => "Open source",
                "About.OpenSource.Description" => "AGPL",
                "About.GitHub.Title" => "GitHub",
                "About.GitHub.Description" => "Project",
                "About.OpenGitHub" => "Open GitHub",
                "About.Mihomo.Title" => "mihomo",
                "About.Mihomo.Description" => "Core",
                "About.OpenMihomo" => "Open mihomo",
                "About.Version.Title" => "Version",
                "About.Runtime.Title" => "Runtime",
                "About.License.Title" => "License",
                "About.Mihomo.Loading" => "Loading",
                "About.Mihomo.Available.Format" => "mihomo available: {0}",
                "About.Mihomo.Unavailable" => "Unavailable",
                _ => key,
            };
        }
    }

    /// <summary>Fake rule catalog for rules tests.</summary>
    private sealed class FakeRuleCatalog : IRuleCatalog
    {
        /// <summary>Gets fake rule rows.</summary>
        /// <value>Current fake rule rows.</value>
        public IReadOnlyList<RulePreview> Rules { get; } =
        [
            new("provider", "DOMAIN", "example.com", "PROXY", 1),
        ];

        /// <summary>Gets fake rule rows.</summary>
        /// <returns>Configured rule rows.</returns>
        public IReadOnlyList<RulePreview> GetRules()
        {
            return Rules;
        }
    }

    /// <summary>Fake statistics store for statistics tests.</summary>
    private sealed class FakeStatisticsStore : IStatisticsStore
    {
        /// <summary>Gets fake daily traffic rows.</summary>
        /// <value>Configured daily rows.</value>
        public IReadOnlyList<TrafficStatisticRow> DailyRows { get; } =
        [
            new("2026-06-17", 1, 2, 3, DateTimeOffset.UnixEpoch),
        ];

        /// <summary>Gets fake node traffic rows.</summary>
        /// <value>Configured node rows.</value>
        public IReadOnlyList<TrafficStatisticRow> NodeRows { get; } =
        [
            new("Node", 4, 5, 0, DateTimeOffset.UnixEpoch),
        ];

        /// <summary>Gets fake statistics summary.</summary>
        /// <returns>Configured statistics summary.</returns>
        public StatisticsSummary GetTrafficStatisticsSummary()
        {
            return new StatisticsSummary(1024, 2048, 3, 5, 1, 2, 4, 6);
        }

        /// <summary>Gets fake profile traffic rows.</summary>
        /// <param name="limit">Maximum row count.</param>
        /// <returns>Configured profile row.</returns>
        public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit)
        {
            return [new("profile-1", 10, 20, 1, DateTimeOffset.UnixEpoch)];
        }

        /// <summary>Gets fake daily traffic rows.</summary>
        /// <param name="limit">Maximum row count.</param>
        /// <returns>Configured daily rows.</returns>
        public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit)
        {
            return DailyRows;
        }

        /// <summary>Gets fake node traffic rows.</summary>
        /// <param name="limit">Maximum row count.</param>
        /// <returns>Configured node rows.</returns>
        public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit)
        {
            return NodeRows;
        }
    }

    /// <summary>Fake profile catalog for statistics tests.</summary>
    private sealed class FakeStatisticsProfiles : IStatisticsProfiles
    {
        /// <summary>Gets profile display names keyed by identifier.</summary>
        /// <returns>Configured profile display names.</returns>
        public IReadOnlyDictionary<string, string> GetProfileDisplayNamesById()
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["profile-1"] = "Active profile",
            };
        }
    }

    /// <summary>Fake about core for about tests.</summary>
    private sealed class FakeAboutCore : IAboutCore
    {
        /// <summary>Gets or sets fake version text.</summary>
        /// <value>Configured version text.</value>
        public string VersionText { get; set; } = "mihomo";

        /// <summary>Gets fake version text.</summary>
        /// <param name="cancellationToken">Cancellation token observed by the fake.</param>
        /// <returns>Configured version text.</returns>
        public Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
        {
            return Task.FromResult(VersionText);
        }
    }

    /// <summary>Fake URI launcher for about tests.</summary>
    private sealed class FakeUriLauncher : IUriLauncher
    {
        /// <summary>Gets launched URIs.</summary>
        /// <value>Mutable list of launched URIs.</value>
        public List<Uri> LaunchedUris { get; } = [];

        /// <summary>Captures one launched URI.</summary>
        /// <param name="uri">URI to launch. Must not be null.</param>
        /// <param name="cancellationToken">Cancellation token accepted for command shape.</param>
        /// <returns>A completed task after capture.</returns>
        public Task LaunchAsync(Uri uri, CancellationToken cancellationToken)
        {
            LaunchedUris.Add(uri);
            return Task.CompletedTask;
        }
    }
}
