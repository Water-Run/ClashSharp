/*
 * Display Page ViewModels
 * Provides MVVM state for read-oriented rules, statistics, and about pages
 *
 * @author: WaterRun
 * @file: ViewModel/DisplayPageViewModels.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using Windows.ApplicationModel;

namespace ClashSharp.ViewModel;

/// <summary>Localization contract shared by read-oriented display page view models.</summary>
/// <remarks>
/// Invariants: Implementations return a non-null string for every key.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: None required by the contract.
/// </remarks>
internal interface IDisplayPageLocalization
{
    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Localized string or fallback text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    string GetString(string key);
}

/// <summary>Rule catalog contract used by <see cref="RulesViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned rule rows are safe to bind directly.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May read active profile rule metadata.
/// </remarks>
internal interface IRuleCatalog
{
    /// <summary>Gets visible rule preview rows.</summary>
    /// <returns>Read-only rule preview rows.</returns>
    IReadOnlyList<RulePreview> GetRules();
}

/// <summary>Statistics summary used by <see cref="StatisticsViewModel"/>.</summary>
/// <param name="TotalUploadBytes">Total uploaded byte count.</param>
/// <param name="TotalDownloadBytes">Total downloaded byte count.</param>
/// <param name="ConnectionCount">Total connection count.</param>
/// <param name="SnapshotCount">Total snapshot count.</param>
/// <param name="ProfileCount">Profile aggregation row count.</param>
/// <param name="NodeCount">Node aggregation row count.</param>
/// <param name="NodeHealthCount">Node health row count.</param>
/// <param name="RuleCount">Rule row count.</param>
/// <remarks>
/// Invariants: Count and byte values are non-negative snapshots.
/// Thread safety: Immutable value type and inherently thread-safe after construction.
/// Side effects: None.
/// </remarks>
internal readonly record struct StatisticsSummary(
    long TotalUploadBytes,
    long TotalDownloadBytes,
    long ConnectionCount,
    long SnapshotCount,
    long ProfileCount,
    long NodeCount,
    long NodeHealthCount,
    long RuleCount);

/// <summary>Statistics data contract used by <see cref="StatisticsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Returned row lists are safe to bind directly.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May read persistent statistics storage.
/// </remarks>
internal interface IStatisticsStore
{
    /// <summary>Gets the current aggregate statistics summary.</summary>
    /// <returns>Current aggregate statistics summary.</returns>
    StatisticsSummary GetTrafficStatisticsSummary();

    /// <summary>Gets profile traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Profile traffic rows.</returns>
    IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit);

    /// <summary>Gets daily traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Daily traffic rows.</returns>
    IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit);

    /// <summary>Gets node traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Node traffic rows.</returns>
    IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit);
}

/// <summary>Profile lookup contract used by <see cref="StatisticsViewModel"/>.</summary>
/// <remarks>
/// Invariants: Keys are profile identifiers and values are display names.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May read profile catalog metadata.
/// </remarks>
internal interface IStatisticsProfiles
{
    /// <summary>Gets profile display names keyed by profile identifier.</summary>
    /// <returns>Profile display names keyed by identifier.</returns>
    IReadOnlyDictionary<string, string> GetProfileDisplayNamesById();
}

/// <summary>Core version contract used by <see cref="AboutViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations return a non-empty version string when the core is available.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: May start a short-lived version probe process.
/// </remarks>
internal interface IAboutCore
{
    /// <summary>Gets bundled core version text.</summary>
    /// <param name="cancellationToken">Cancels the version probe when requested.</param>
    /// <returns>Version text.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the version probe.
    /// Completion semantics: Does not mutate long-running core state.
    /// </remarks>
    Task<string> GetVersionTextAsync(CancellationToken cancellationToken);
}

/// <summary>URI launcher contract used by <see cref="AboutViewModel"/>.</summary>
/// <remarks>
/// Invariants: Implementations attempt to open the supplied URI.
/// Thread safety: Determined by the concrete implementation.
/// Side effects: Opens an external URI.
/// </remarks>
internal interface IUriLauncher
{
    /// <summary>Launches the supplied URI.</summary>
    /// <param name="uri">URI to launch. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token accepted for command consistency.</param>
    /// <returns>A task that completes after the launch request has been submitted.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
    /// <remarks>
    /// Cancellation semantics: Determined by the concrete implementation.
    /// Completion semantics: Completion does not guarantee the external application remains open.
    /// </remarks>
    Task LaunchAsync(Uri uri, CancellationToken cancellationToken);
}

/// <summary>Bindable view model for the rules page.</summary>
/// <remarks>
/// Invariants: Rule rows are loaded during construction and are never null.
/// Thread safety: Not thread-safe; intended for UI-thread binding.
/// Side effects: Reads injected rule catalog during construction.
/// </remarks>
internal sealed class RulesViewModel
{
    /// <summary>Localization provider used by this view model.</summary>
    private readonly IDisplayPageLocalization _localization;

    /// <summary>Initializes a rules view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="rules">Rule catalog. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> or <paramref name="rules"/> is null.</exception>
    public RulesViewModel(IDisplayPageLocalization localization, IRuleCatalog rules)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        ArgumentNullException.ThrowIfNull(rules);
        Rules = rules.GetRules();
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _localization.GetString("Nav.Rules");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _localization.GetString("Page.Rules.Description");

    /// <summary>Gets rule preview rows.</summary>
    /// <value>Rule preview rows; never null.</value>
    public IReadOnlyList<RulePreview> Rules { get; }
}

/// <summary>Bindable view model for the statistics page.</summary>
/// <remarks>
/// Invariants: Summary and row properties are non-null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding.
/// Side effects: Reads injected statistics services during refresh.
/// </remarks>
internal sealed class StatisticsViewModel : ObservableObject
{
    /// <summary>Localization provider used by this view model.</summary>
    private readonly IDisplayPageLocalization _localization;

    /// <summary>Statistics store used by refresh operations.</summary>
    private readonly IStatisticsStore _statistics;

    /// <summary>Profile lookup used to resolve profile identifiers.</summary>
    private readonly IStatisticsProfiles _profiles;

    /// <summary>Navigation action used by <see cref="OpenLogsCommand"/>.</summary>
    private readonly Action _openLogs;

    /// <summary>Backing field for <see cref="TotalTrafficText"/>.</summary>
    private string _totalTrafficText = string.Empty;

    /// <summary>Backing field for <see cref="ConnectionCountText"/>.</summary>
    private string _connectionCountText = string.Empty;

    /// <summary>Backing field for <see cref="ProfileStatisticText"/>.</summary>
    private string _profileStatisticText = string.Empty;

    /// <summary>Backing field for <see cref="SnapshotStatisticText"/>.</summary>
    private string _snapshotStatisticText = string.Empty;

    /// <summary>Backing field for <see cref="NodeStatisticText"/>.</summary>
    private string _nodeStatisticText = string.Empty;

    /// <summary>Backing field for <see cref="RuleStatisticText"/>.</summary>
    private string _ruleStatisticText = string.Empty;

    /// <summary>Backing field for <see cref="ProfileTrafficRows"/>.</summary>
    private IReadOnlyList<TrafficStatisticRow> _profileTrafficRows = [];

    /// <summary>Backing field for <see cref="DailyTrafficRows"/>.</summary>
    private IReadOnlyList<TrafficStatisticRow> _dailyTrafficRows = [];

    /// <summary>Backing field for <see cref="NodeTrafficRows"/>.</summary>
    private IReadOnlyList<TrafficStatisticRow> _nodeTrafficRows = [];

    /// <summary>Initializes a statistics view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="statistics">Statistics store. Must not be null.</param>
    /// <param name="profiles">Profile lookup. Must not be null.</param>
    /// <param name="openLogs">Navigation action. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public StatisticsViewModel(
        IDisplayPageLocalization localization,
        IStatisticsStore statistics,
        IStatisticsProfiles profiles,
        Action openLogs)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _openLogs = openLogs ?? throw new ArgumentNullException(nameof(openLogs));
        OpenLogsCommand = new RelayCommand(_openLogs);
        Refresh();
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _localization.GetString("Nav.Statistics");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _localization.GetString("Page.Statistics.Description");

    /// <summary>Gets the total statistics card title.</summary>
    /// <value>Localized card title.</value>
    public string TotalStatisticsTitleText => _localization.GetString("Statistics.Total.Title");

    /// <summary>Gets the profile statistics card title.</summary>
    /// <value>Localized card title.</value>
    public string ProfileStatisticsTitleText => _localization.GetString("Statistics.Profile.Title");

    /// <summary>Gets the node statistics card title.</summary>
    /// <value>Localized card title.</value>
    public string NodeStatisticsTitleText => _localization.GetString("Statistics.Node.Title");

    /// <summary>Gets the profile breakdown title.</summary>
    /// <value>Localized section title.</value>
    public string ByProfileTitleText => _localization.GetString("Statistics.ByProfile.Title");

    /// <summary>Gets the date breakdown title.</summary>
    /// <value>Localized section title.</value>
    public string ByDateTitleText => _localization.GetString("Statistics.ByDate.Title");

    /// <summary>Gets the node breakdown title.</summary>
    /// <value>Localized section title.</value>
    public string ByNodeTitleText => _localization.GetString("Statistics.ByNode.Title");

    /// <summary>Gets the log shortcut title.</summary>
    /// <value>Localized shortcut title.</value>
    public string LogsShortcutTitleText => _localization.GetString("Statistics.LogsShortcut.Title");

    /// <summary>Gets the log shortcut description.</summary>
    /// <value>Localized shortcut description.</value>
    public string LogsShortcutDescriptionText => _localization.GetString("Statistics.LogsShortcut.Description");

    /// <summary>Gets the open logs command text.</summary>
    /// <value>Localized command text.</value>
    public string OpenLogsText => _localization.GetString("Statistics.OpenLogs");

    /// <summary>Gets formatted total traffic text.</summary>
    /// <value>Formatted total traffic text.</value>
    public string TotalTrafficText
    {
        get => _totalTrafficText;
        private set => SetProperty(ref _totalTrafficText, value);
    }

    /// <summary>Gets formatted connection count text.</summary>
    /// <value>Formatted connection count text.</value>
    public string ConnectionCountText
    {
        get => _connectionCountText;
        private set => SetProperty(ref _connectionCountText, value);
    }

    /// <summary>Gets formatted profile count text.</summary>
    /// <value>Formatted profile count text.</value>
    public string ProfileStatisticText
    {
        get => _profileStatisticText;
        private set => SetProperty(ref _profileStatisticText, value);
    }

    /// <summary>Gets formatted snapshot count text.</summary>
    /// <value>Formatted snapshot count text.</value>
    public string SnapshotStatisticText
    {
        get => _snapshotStatisticText;
        private set => SetProperty(ref _snapshotStatisticText, value);
    }

    /// <summary>Gets formatted node count text.</summary>
    /// <value>Formatted node count text.</value>
    public string NodeStatisticText
    {
        get => _nodeStatisticText;
        private set => SetProperty(ref _nodeStatisticText, value);
    }

    /// <summary>Gets formatted rule count text.</summary>
    /// <value>Formatted rule count text.</value>
    public string RuleStatisticText
    {
        get => _ruleStatisticText;
        private set => SetProperty(ref _ruleStatisticText, value);
    }

    /// <summary>Gets profile traffic rows.</summary>
    /// <value>Profile traffic rows with current names applied.</value>
    public IReadOnlyList<TrafficStatisticRow> ProfileTrafficRows
    {
        get => _profileTrafficRows;
        private set => SetProperty(ref _profileTrafficRows, value);
    }

    /// <summary>Gets daily traffic rows.</summary>
    /// <value>Daily traffic rows.</value>
    public IReadOnlyList<TrafficStatisticRow> DailyTrafficRows
    {
        get => _dailyTrafficRows;
        private set => SetProperty(ref _dailyTrafficRows, value);
    }

    /// <summary>Gets node traffic rows.</summary>
    /// <value>Node traffic rows.</value>
    public IReadOnlyList<TrafficStatisticRow> NodeTrafficRows
    {
        get => _nodeTrafficRows;
        private set => SetProperty(ref _nodeTrafficRows, value);
    }

    /// <summary>Gets the command that navigates to logs.</summary>
    /// <value>Synchronous navigation command.</value>
    public RelayCommand OpenLogsCommand { get; }

    /// <summary>Refreshes statistics summary and row collections.</summary>
    public void Refresh()
    {
        StatisticsSummary summary = _statistics.GetTrafficStatisticsSummary();
        TotalTrafficText = string.Format(
            _localization.GetString("Statistics.TotalTraffic.Format"),
            FormatByteCount(summary.TotalUploadBytes),
            FormatByteCount(summary.TotalDownloadBytes));
        ConnectionCountText = string.Format(_localization.GetString("Statistics.ConnectionCount.Format"), summary.ConnectionCount);
        ProfileStatisticText = string.Format(_localization.GetString("Statistics.ProfileCount.Format"), summary.ProfileCount);
        SnapshotStatisticText = string.Format(_localization.GetString("Statistics.SnapshotCount.Format"), summary.SnapshotCount);
        NodeStatisticText = string.Format(_localization.GetString("Statistics.NodeCount.Format"), summary.NodeCount, summary.NodeHealthCount);
        RuleStatisticText = string.Format(_localization.GetString("Statistics.RuleCount.Format"), summary.RuleCount);
        ProfileTrafficRows = ResolveProfileTrafficRows(_statistics.GetProfileTrafficRows(10));
        DailyTrafficRows = _statistics.GetDailyTrafficRows(14);
        NodeTrafficRows = _statistics.GetNodeTrafficRows(10);
    }

    /// <summary>Formats a byte count for compact UI display.</summary>
    /// <param name="bytes">Byte count.</param>
    /// <returns>Formatted byte count.</returns>
    private static string FormatByteCount(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = Math.Max(0, bytes);
        int unitIndex = 0;
        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:N1} {units[unitIndex]}";
    }

    /// <summary>Applies current profile display names to profile traffic rows.</summary>
    /// <param name="rows">Stored profile traffic rows. Must not be null.</param>
    /// <returns>Rows with display names applied when available.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="rows"/> is null.</exception>
    private IReadOnlyList<TrafficStatisticRow> ResolveProfileTrafficRows(IReadOnlyList<TrafficStatisticRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        IReadOnlyDictionary<string, string> profileNames = _profiles.GetProfileDisplayNamesById();
        List<TrafficStatisticRow> resolvedRows = new(rows.Count);
        foreach (TrafficStatisticRow row in rows)
        {
            string label = profileNames.TryGetValue(row.Label, out string? profileName) ? profileName : row.Label;
            resolvedRows.Add(row with { Label = label });
        }

        return resolvedRows;
    }
}

/// <summary>Bindable view model for the about page.</summary>
/// <remarks>
/// Invariants: Static labels are available immediately and mihomo status is non-null.
/// Thread safety: Not thread-safe; intended for UI-thread binding.
/// Side effects: Commands can launch external URIs and load can probe the core binary.
/// </remarks>
internal sealed class AboutViewModel : ObservableObject
{
    /// <summary>Clash# repository URL.</summary>
    private static readonly Uri GitHubUri = new("https://github.com/Water-Run/ClashSharp");

    /// <summary>mihomo upstream repository URL.</summary>
    private static readonly Uri MihomoUri = new("https://github.com/MetaCubeX/mihomo");

    /// <summary>Localization provider used by this view model.</summary>
    private readonly IDisplayPageLocalization _localization;

    /// <summary>Core provider used by status loading.</summary>
    private readonly IAboutCore _core;

    /// <summary>URI launcher used by link commands.</summary>
    private readonly IUriLauncher _launcher;

    /// <summary>Backing field for <see cref="MihomoStatusText"/>.</summary>
    private string _mihomoStatusText = string.Empty;

    /// <summary>Initializes an about view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="core">Core provider. Must not be null.</param>
    /// <param name="launcher">URI launcher. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public AboutViewModel(IDisplayPageLocalization localization, IAboutCore core, IUriLauncher launcher)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _core = core ?? throw new ArgumentNullException(nameof(core));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
        MihomoStatusText = _localization.GetString("About.Mihomo.Loading");
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        OpenGitHubCommand = new AsyncRelayCommand((_, token) => _launcher.LaunchAsync(GitHubUri, token));
        OpenMihomoCommand = new AsyncRelayCommand((_, token) => _launcher.LaunchAsync(MihomoUri, token));
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _localization.GetString("Nav.About");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _localization.GetString("Page.About.Description");

    /// <summary>Gets the app description text.</summary>
    /// <value>Localized app description.</value>
    public string AppDescriptionText => _localization.GetString("About.App.Description");

    /// <summary>Gets the application name text.</summary>
    /// <value>Application display name.</value>
    public string AppNameText => "Clash#";

    /// <summary>Gets the application version text.</summary>
    /// <value>Application package or assembly version.</value>
    public string VersionText => GetVersionText();

    /// <summary>Gets the author title text.</summary>
    /// <value>Localized author title.</value>
    public string AuthorTitleText => _localization.GetString("About.Author.Title");

    /// <summary>Gets the author value text.</summary>
    /// <value>Localized author value.</value>
    public string AuthorValueText => _localization.GetString("About.Author.Value");

    /// <summary>Gets the open-source title text.</summary>
    /// <value>Localized open-source title.</value>
    public string OpenSourceTitleText => _localization.GetString("About.OpenSource.Title");

    /// <summary>Gets the open-source description text.</summary>
    /// <value>Localized open-source description.</value>
    public string OpenSourceDescriptionText => _localization.GetString("About.OpenSource.Description");

    /// <summary>Gets the protocol title text.</summary>
    /// <value>Localized protocol title.</value>
    public string ProtocolTitleText => _localization.GetString("About.Protocol.Title");

    /// <summary>Gets the protocol value text.</summary>
    /// <value>Localized protocol value.</value>
    public string ProtocolValueText => _localization.GetString("About.Protocol.Value");

    /// <summary>Gets the GitHub title text.</summary>
    /// <value>Localized GitHub title.</value>
    public string GitHubTitleText => _localization.GetString("About.GitHub.Title");

    /// <summary>Gets the GitHub description text.</summary>
    /// <value>Localized GitHub description.</value>
    public string GitHubDescriptionText => _localization.GetString("About.GitHub.Description");

    /// <summary>Gets the GitHub button text.</summary>
    /// <value>Localized GitHub button text.</value>
    public string GitHubButtonText => _localization.GetString("About.OpenGitHub");

    /// <summary>Gets the mihomo title text.</summary>
    /// <value>Localized mihomo title.</value>
    public string MihomoTitleText => _localization.GetString("About.Mihomo.Title");

    /// <summary>Gets the mihomo description text.</summary>
    /// <value>Localized mihomo description.</value>
    public string MihomoDescriptionText => _localization.GetString("About.Mihomo.Description");

    /// <summary>Gets the mihomo button text.</summary>
    /// <value>Localized mihomo button text.</value>
    public string MihomoButtonText => _localization.GetString("About.OpenMihomo");

    /// <summary>Gets the local proxy information title text.</summary>
    /// <value>Localized proxy information title.</value>
    public string ProxyInformationTitleText => _localization.GetString("About.ProxyInformation.Title");

    /// <summary>Gets the local proxy information description text.</summary>
    /// <value>Localized proxy information description.</value>
    public string ProxyInformationDescriptionText => _localization.GetString("About.ProxyInformation.Description");

    /// <summary>Gets the proxy information button text.</summary>
    /// <value>Localized proxy information button text.</value>
    public string ProxyInformationButtonText => _localization.GetString("About.ProxyInformation.Open");

    /// <summary>Gets bundled mihomo status text.</summary>
    /// <value>Status text; never null.</value>
    public string MihomoStatusText
    {
        get => _mihomoStatusText;
        private set => SetProperty(ref _mihomoStatusText, value);
    }

    /// <summary>Gets the command that loads mihomo status.</summary>
    /// <value>Asynchronous load command.</value>
    public AsyncRelayCommand LoadCommand { get; }

    /// <summary>Gets the command that opens the project repository.</summary>
    /// <value>Asynchronous URI launch command.</value>
    public AsyncRelayCommand OpenGitHubCommand { get; }

    /// <summary>Gets the command that opens the upstream mihomo repository.</summary>
    /// <value>Asynchronous URI launch command.</value>
    public AsyncRelayCommand OpenMihomoCommand { get; }

    /// <summary>Loads bundled mihomo version status.</summary>
    /// <param name="cancellationToken">Cancels version probing when requested.</param>
    /// <returns>A task that completes after status text is updated.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the core provider.
    /// Thread / reentrancy: UI callers should use <see cref="LoadCommand"/> to prevent reentrancy.
    /// </remarks>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            string versionText = await _core.GetVersionTextAsync(cancellationToken);
            MihomoStatusText = string.Format(_localization.GetString("About.Mihomo.Available.Format"), versionText);
        }
        catch (Exception exception) when (exception is FileNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            MihomoStatusText = _localization.GetString("About.Mihomo.Unavailable");
        }
    }

    /// <summary>Resolves the current application version for display.</summary>
    /// <returns>Package version when available; otherwise assembly version.</returns>
    private static string GetVersionText()
    {
        try
        {
            PackageVersion version = Package.Current.Id.Version;
            return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
        }
        catch (InvalidOperationException)
        {
            return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0";
        }
    }
}
