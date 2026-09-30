using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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

    private readonly Func<DateTimeOffset> _getNow;
    private int _loadRevision;
    private bool _isLoading;
    private bool _hasSnapshot;
    private bool _hasLoadError;
    private DateTimeOffset? _lastUpdated;

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
    private IReadOnlyList<StatisticsTrafficRowDisplay> _profileTrafficRows = [];

    /// <summary>Backing field for <see cref="DailyTrafficRows"/>.</summary>
    private IReadOnlyList<StatisticsTrafficRowDisplay> _dailyTrafficRows = [];

    /// <summary>Backing field for <see cref="NodeTrafficRows"/>.</summary>
    private IReadOnlyList<StatisticsTrafficRowDisplay> _nodeTrafficRows = [];

    private IReadOnlyList<StatisticsRuleHitDisplay> _ruleHitRows = [];

    /// <summary>Initializes a statistics view model.</summary>
    /// <param name="localization">Localization provider. Must not be null.</param>
    /// <param name="statistics">Statistics store. Must not be null.</param>
    /// <param name="profiles">Profile lookup. Must not be null.</param>
    /// <param name="openLogs">Navigation action. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="displayMapper">UI display row mapper. Must not be null.</param>
    /// <param name="getNow">Optional clock for the last successful refresh time.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public StatisticsViewModel(
        IDisplayPageLocalization localization,
        IStatisticsStore statistics,
        IStatisticsProfiles profiles,
        Action openLogs,
        IApplicationErrorSink errorSink,
        IModelDisplayMapper displayMapper,
        Func<DateTimeOffset>? getNow = null)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
        _statistics = statistics ?? throw new ArgumentNullException(nameof(statistics));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _openLogs = openLogs ?? throw new ArgumentNullException(nameof(openLogs));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _displayMapper = displayMapper ?? throw new ArgumentNullException(nameof(displayMapper));
        _getNow = getNow ?? (static () => DateTimeOffset.UtcNow);
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

    public string ByRuleTitleText => _localization.GetString("Statistics.ByRule.Title");

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
    public IReadOnlyList<StatisticsTrafficRowDisplay> ProfileTrafficRows
    {
        get => _profileTrafficRows;
        private set => SetProperty(ref _profileTrafficRows, value);
    }

    /// <summary>Gets daily traffic rows.</summary>
    /// <value>Daily traffic rows.</value>
    public IReadOnlyList<StatisticsTrafficRowDisplay> DailyTrafficRows
    {
        get => _dailyTrafficRows;
        private set => SetProperty(ref _dailyTrafficRows, value);
    }

    /// <summary>Gets node traffic rows.</summary>
    /// <value>Node traffic rows.</value>
    public IReadOnlyList<StatisticsTrafficRowDisplay> NodeTrafficRows
    {
        get => _nodeTrafficRows;
        private set => SetProperty(ref _nodeTrafficRows, value);
    }

    /// <summary>Gets the ten most-hit rules, with deterministic ordering for equal counts.</summary>
    public IReadOnlyList<StatisticsRuleHitDisplay> RuleHitRows => _ruleHitRows;

    /// <summary>Gets the command that navigates to logs.</summary>
    /// <value>Synchronous navigation command.</value>
    public RelayCommand OpenLogsCommand { get; }

    public string RefreshText => _localization.GetString("Command.Refresh");

    public string LoadingText => _localization.GetString("Statistics.Loading");

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (SetProperty(ref _isLoading, value))
            {
                OnPropertyChanged(nameof(CanRefresh));
                OnPropertyChanged(nameof(StatusText));
                NotifyEmptyStates();
            }
        }
    }

    public bool CanRefresh => !IsLoading;

    public bool HasSnapshot => _hasSnapshot;

    public bool HasLoadError
    {
        get => _hasLoadError;
        private set
        {
            if (SetProperty(ref _hasLoadError, value))
            {
                OnPropertyChanged(nameof(LoadErrorText));
                NotifyEmptyStates();
            }
        }
    }

    public string LoadErrorText => HasLoadError
        ? _localization.GetString(HasSnapshot ? "Statistics.RefreshFailed" : "Statistics.LoadFailed") : string.Empty;

    public string StatusText => IsLoading ? LoadingText : _lastUpdated is DateTimeOffset updated
        ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Updated.Format"), updated.ToLocalTime().ToString("G", CultureInfo.CurrentCulture))
        : string.Empty;

    public bool HasNoProfileTraffic => HasSnapshot && !IsLoading && !HasLoadError && ProfileTrafficRows.Count == 0;

    public bool HasNoDailyTraffic => HasSnapshot && !IsLoading && !HasLoadError && DailyTrafficRows.Count == 0;

    public bool HasNoNodeTraffic => HasSnapshot && !IsLoading && !HasLoadError && NodeTrafficRows.Count == 0;

    public bool HasNoRuleHits => HasSnapshot && !IsLoading && !HasLoadError && RuleHitRows.Count == 0;

    public string NoProfileTrafficText => _localization.GetString("Statistics.Empty.Profile");

    public string NoDailyTrafficText => _localization.GetString("Statistics.Empty.Date");

    public string NoNodeTrafficText => _localization.GetString("Statistics.Empty.Node");

    public string NoRuleHitsText => _localization.GetString("Statistics.Empty.Rule");

    /// <summary>Loads statistics without blocking the UI thread.</summary>
    /// <param name="cancellationToken">Cancels this page-load attempt.</param>
    /// <returns>A task that completes after the snapshot is applied or the failure is reported.</returns>
    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested) { return; }
        int revision = ++_loadRevision;
        IsLoading = true;
        HasLoadError = false;
        try
        {
            bool loaded = await ViewModelLoadExecutor.ExecuteAsync(
                ReadLoadSnapshot, ApplyLoadSnapshot, _errorSink, "statistics-load", cancellationToken,
                () => revision == _loadRevision);
            if (revision == _loadRevision && !cancellationToken.IsCancellationRequested)
            {
                HasLoadError = !loaded;
            }
        }
        finally
        {
            if (revision == _loadRevision) { IsLoading = false; }
        }
    }

    /// <summary>Does not carry totals or rows from a previous data directory into a failed refresh.</summary>
    public Task ReloadForDataChangeAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ++_loadRevision;
        _hasSnapshot = false;
        _lastUpdated = null;
        _totalTrafficText = _connectionCountText = _profileStatisticText = _snapshotStatisticText = _nodeStatisticText = _ruleStatisticText = string.Empty;
        _profileTrafficRows = [];
        _dailyTrafficRows = [];
        _nodeTrafficRows = [];
        _ruleHitRows = [];
        foreach (string property in new[] { nameof(TotalTrafficText), nameof(ConnectionCountText), nameof(ProfileStatisticText),
            nameof(SnapshotStatisticText), nameof(NodeStatisticText), nameof(RuleStatisticText), nameof(ProfileTrafficRows),
            nameof(DailyTrafficRows), nameof(NodeTrafficRows), nameof(RuleHitRows), nameof(HasSnapshot), nameof(StatusText) })
        {
            OnPropertyChanged(property);
        }
        return LoadAsync(cancellationToken);
    }

    private StatisticsLoadSnapshot ReadLoadSnapshot()
    {
        StatisticsSummary summary = _statistics.GetTrafficStatisticsSummary();
        return new StatisticsLoadSnapshot(
            summary,
            _statistics.GetProfileTrafficRows(10),
            _statistics.GetDailyTrafficRows(14),
            _statistics.GetNodeTrafficRows(10),
            _statistics.GetRuleHitCounts()
                .OrderByDescending(static row => row.Value)
                .ThenBy(static row => row.Key, StringComparer.Ordinal)
                .Take(10).ToArray(),
            _profiles.GetProfileDisplayNamesById());
    }

    private void ApplyLoadSnapshot(StatisticsLoadSnapshot snapshot)
    {
        StatisticsSummary summary = snapshot.Summary;
        string totalTraffic = string.Format(
            CultureInfo.CurrentCulture,
            _localization.GetString("Statistics.TotalTraffic.Format"),
            FormatByteCount(summary.TotalUploadBytes),
            FormatByteCount(summary.TotalDownloadBytes));
        string connections = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.ConnectionCount.Format"), summary.ConnectionCount);
        string profiles = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.ProfileCount.Format"), summary.ProfileCount);
        string snapshots = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.SnapshotCount.Format"), summary.SnapshotCount);
        string nodes = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.NodeCount.Format"), summary.NodeCount, summary.NodeHealthCount);
        string rules = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.RuleCount.Format"), summary.RuleCount);
        IReadOnlyList<StatisticsTrafficRowDisplay> profileRows = FormatTrafficRows(snapshot.ProfileTrafficRows,
            "Statistics.ConnectionCount.Format", snapshot.ProfileNames);
        IReadOnlyList<StatisticsTrafficRowDisplay> dailyRows = FormatTrafficRows(snapshot.DailyTrafficRows,
            "Statistics.SnapshotCount.Format", daily: true);
        IReadOnlyList<StatisticsTrafficRowDisplay> nodeRows = FormatTrafficRows(snapshot.NodeTrafficRows,
            "Statistics.SnapshotCount.Format");
        StatisticsRuleHitDisplay[] ruleRows = snapshot.RuleHitCounts.Select(row => new StatisticsRuleHitDisplay(
            _displayMapper.MapText(row.Key), row.Value,
            string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.RuleHits.Format"), row.Value))).ToArray();
        DateTimeOffset updated = _getNow();

        // Prepare every value first, then publish one complete snapshot to binding observers.
        _totalTrafficText = totalTraffic;
        _connectionCountText = connections;
        _profileStatisticText = profiles;
        _snapshotStatisticText = snapshots;
        _nodeStatisticText = nodes;
        _ruleStatisticText = rules;
        _profileTrafficRows = profileRows;
        _dailyTrafficRows = dailyRows;
        _nodeTrafficRows = nodeRows;
        _ruleHitRows = ruleRows;
        _lastUpdated = updated;
        _hasSnapshot = true;
        OnPropertyChanged(nameof(TotalTrafficText));
        OnPropertyChanged(nameof(ConnectionCountText));
        OnPropertyChanged(nameof(ProfileStatisticText));
        OnPropertyChanged(nameof(SnapshotStatisticText));
        OnPropertyChanged(nameof(NodeStatisticText));
        OnPropertyChanged(nameof(RuleStatisticText));
        OnPropertyChanged(nameof(ProfileTrafficRows));
        OnPropertyChanged(nameof(DailyTrafficRows));
        OnPropertyChanged(nameof(NodeTrafficRows));
        OnPropertyChanged(nameof(RuleHitRows));
        OnPropertyChanged(nameof(HasSnapshot));
        OnPropertyChanged(nameof(StatusText));
        NotifyEmptyStates();
    }

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(HasNoProfileTraffic));
        OnPropertyChanged(nameof(HasNoDailyTraffic));
        OnPropertyChanged(nameof(HasNoNodeTraffic));
        OnPropertyChanged(nameof(HasNoRuleHits));
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

    private IReadOnlyList<StatisticsTrafficRowDisplay> FormatTrafficRows(
        IReadOnlyList<TrafficStatisticRow> rows,
        string countFormat,
        IReadOnlyDictionary<string, string>? profileNames = null,
        bool daily = false)
    {
        List<StatisticsTrafficRowDisplay> resolvedRows = new(rows.Count);
        foreach (TrafficStatisticRow row in rows)
        {
            string rawLabel = profileNames is not null && profileNames.TryGetValue(row.Label, out string? profileName)
                ? profileName
                : row.Label;
            string label = daily && DateOnly.TryParseExact(rawLabel, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date)
                ? date.ToString("d", CultureInfo.CurrentCulture)
                : _displayMapper.MapText(rawLabel);
            resolvedRows.Add(new StatisticsTrafficRowDisplay(row, label,
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Total.Format"), row.TotalDisplay),
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.TotalTraffic.Format"), row.UploadDisplay, row.DownloadDisplay),
                string.Format(CultureInfo.CurrentCulture, _localization.GetString(countFormat), row.SampleCount),
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Updated.Format"),
                    row.UpdatedAt.ToLocalTime().ToString("G", CultureInfo.CurrentCulture))));
        }

        return resolvedRows;
    }

    private sealed record StatisticsLoadSnapshot(
        StatisticsSummary Summary,
        IReadOnlyList<TrafficStatisticRow> ProfileTrafficRows,
        IReadOnlyList<TrafficStatisticRow> DailyTrafficRows,
        IReadOnlyList<TrafficStatisticRow> NodeTrafficRows,
        IReadOnlyList<KeyValuePair<string, long>> RuleHitCounts,
        IReadOnlyDictionary<string, string> ProfileNames);
}
