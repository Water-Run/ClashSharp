using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for the statistics page.</summary>
/// <remarks>
/// Invariants: Summary and row properties are non-null and remain empty until loading succeeds.
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

    /// <summary>Reports unexpected page-load failures.</summary>
    private readonly IApplicationErrorSink _errorSink;

    /// <summary>Applies the injected UI-only display policy to persisted labels.</summary>
    private readonly IModelDisplayMapper _displayMapper;

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
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="displayMapper">UI display row mapper. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public StatisticsViewModel(
        IDisplayPageLocalization localization,
        IStatisticsStore statistics,
        IStatisticsProfiles profiles,
        Action openLogs,
        IApplicationErrorSink errorSink,
        IModelDisplayMapper displayMapper)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _openLogs = openLogs ?? throw new ArgumentNullException(nameof(openLogs));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _displayMapper = displayMapper ?? throw new ArgumentNullException(nameof(displayMapper));
        OpenLogsCommand = new RelayCommand(_openLogs);
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

    /// <summary>Loads statistics without blocking the UI thread.</summary>
    /// <param name="cancellationToken">Cancels this page-load attempt.</param>
    /// <returns>A task that completes after the snapshot is applied or the failure is reported.</returns>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        return ViewModelLoadExecutor.ExecuteAsync(
            ReadLoadSnapshot,
            ApplyLoadSnapshot,
            _errorSink,
            "statistics-load",
            cancellationToken);
    }

    /// <summary>Refreshes statistics summary and row collections.</summary>
    public void Refresh()
    {
        ApplyLoadSnapshot(ReadLoadSnapshot());
    }

    private StatisticsLoadSnapshot ReadLoadSnapshot()
    {
        StatisticsSummary summary = _statistics.GetTrafficStatisticsSummary();
        return new StatisticsLoadSnapshot(
            summary,
            ResolveProfileTrafficRows(_statistics.GetProfileTrafficRows(10)),
            _statistics.GetDailyTrafficRows(14),
            _statistics.GetNodeTrafficRows(10));
    }

    private void ApplyLoadSnapshot(StatisticsLoadSnapshot snapshot)
    {
        StatisticsSummary summary = snapshot.Summary;
        TotalTrafficText = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Statistics.TotalTraffic.Format"),
            FormatByteCount(summary.TotalUploadBytes),
            FormatByteCount(summary.TotalDownloadBytes));
        ConnectionCountText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.ConnectionCount.Format"), summary.ConnectionCount);
        ProfileStatisticText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.ProfileCount.Format"), summary.ProfileCount);
        SnapshotStatisticText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.SnapshotCount.Format"), summary.SnapshotCount);
        NodeStatisticText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.NodeCount.Format"), summary.NodeCount, summary.NodeHealthCount);
        RuleStatisticText = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.RuleCount.Format"), summary.RuleCount);
        ProfileTrafficRows = snapshot.ProfileTrafficRows;
        DailyTrafficRows = snapshot.DailyTrafficRows;
        NodeTrafficRows = snapshot.NodeTrafficRows;
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
            string rawLabel = profileNames.TryGetValue(row.Label, out string? profileName)
                ? profileName
                : row.Label;
            string label = _displayMapper.MapText(rawLabel);
            resolvedRows.Add(row with { Label = label });
        }

        return resolvedRows;
    }

    private sealed record StatisticsLoadSnapshot(
        StatisticsSummary Summary,
        IReadOnlyList<TrafficStatisticRow> ProfileTrafficRows,
        IReadOnlyList<TrafficStatisticRow> DailyTrafficRows,
        IReadOnlyList<TrafficStatisticRow> NodeTrafficRows);
}
