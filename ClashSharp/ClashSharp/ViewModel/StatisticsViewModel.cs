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
internal sealed class StatisticsViewModel : ObservableObject, ILocalizedPresentation
{
    /// <summary>Refreshes current page text without reloading data or changing user choices.</summary>
    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(DescriptionText));
        OnPropertyChanged(nameof(TotalStatisticsTitleText));
        OnPropertyChanged(nameof(ProfileStatisticsTitleText));
        OnPropertyChanged(nameof(NodeStatisticsTitleText));
        OnPropertyChanged(nameof(ByProfileTitleText));
        OnPropertyChanged(nameof(ByDateTitleText));
        OnPropertyChanged(nameof(ByNodeTitleText));
        OnPropertyChanged(nameof(ByRuleTitleText));
        OnPropertyChanged(nameof(LogsShortcutTitleText));
        OnPropertyChanged(nameof(LogsShortcutDescriptionText));
        OnPropertyChanged(nameof(OpenLogsText));
        OnPropertyChanged(nameof(RefreshText));
        OnPropertyChanged(nameof(LoadingText));
        OnPropertyChanged(nameof(LoadErrorText));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(NoProfileTrafficText));
        OnPropertyChanged(nameof(NoDailyTrafficText));
        OnPropertyChanged(nameof(NoNodeTrafficText));
        OnPropertyChanged(nameof(NoRuleHitsText));
        OnPropertyChanged(nameof(TrafficKpiTitleText));
        OnPropertyChanged(nameof(ConnectionsKpiTitleText));
        OnPropertyChanged(nameof(NodesKpiTitleText));
        OnPropertyChanged(nameof(ProfilesKpiTitleText));
        OnPropertyChanged(nameof(ProfilesKpiCaptionText));
        OnPropertyChanged(nameof(DailyChartTitleText));
        OnPropertyChanged(nameof(NoDailyChartText));
        if (_lastSnapshot is not null) { ApplyLoadSnapshot(_lastSnapshot, recordObservation: false); }
    }

    private StatisticsLoadSnapshot? _lastSnapshot;

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

    private StatisticsKpiDisplay _kpi = StatisticsKpiDisplay.Empty;

    private IReadOnlyList<StatisticsDailyBarDisplay> _dailyChartBars = [];

    private string _dailyChartRangeText = string.Empty;

    private string _dailyChartPeakText = string.Empty;

    /// <summary>Number of calendar days shown by the recent-traffic chart, ending on the refresh day.</summary>
    internal const int DailyChartDays = 14;

    /// <summary>Smallest visible share for a non-zero value so tiny totals do not render as missing.</summary>
    private const double MinimumVisibleShare = 0.02;

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
        ? string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Updated.Format"), _localization.Formatter.Timestamp(updated))
        : string.Empty;

    public bool HasNoProfileTraffic => HasSnapshot && !IsLoading && !HasLoadError && ProfileTrafficRows.Count == 0;

    public bool HasNoDailyTraffic => HasSnapshot && !IsLoading && !HasLoadError && DailyTrafficRows.Count == 0;

    public bool HasNoNodeTraffic => HasSnapshot && !IsLoading && !HasLoadError && NodeTrafficRows.Count == 0;

    public bool HasNoRuleHits => HasSnapshot && !IsLoading && !HasLoadError && RuleHitRows.Count == 0;

    public string NoProfileTrafficText => _localization.GetString("Statistics.Empty.Profile");

    public string NoDailyTrafficText => _localization.GetString("Statistics.Empty.Date");

    public string NoNodeTrafficText => _localization.GetString("Statistics.Empty.Node");

    public string NoRuleHitsText => _localization.GetString("Statistics.Empty.Rule");

    /// <summary>Gets the total-traffic stat tile label.</summary>
    public string TrafficKpiTitleText => _localization.GetString("Statistics.Kpi.Traffic.Title");

    /// <summary>Gets the connection-record stat tile label.</summary>
    public string ConnectionsKpiTitleText => _localization.GetString("Statistics.Kpi.Connections.Title");

    /// <summary>Gets the node stat tile label.</summary>
    public string NodesKpiTitleText => _localization.GetString("Statistics.Kpi.Nodes.Title");

    /// <summary>Gets the profile stat tile label.</summary>
    public string ProfilesKpiTitleText => _localization.GetString("Statistics.Kpi.Profiles.Title");

    /// <summary>Gets the explanatory caption below the profile count.</summary>
    public string ProfilesKpiCaptionText => _localization.GetString("Statistics.Kpi.Profiles.Caption");

    /// <summary>Gets combined upload and download for the headline stat tile.</summary>
    public string TotalTransferValueText => _kpi.TotalTransfer;

    /// <summary>Gets the connection-record count without its unit label.</summary>
    public string ConnectionCountValueText => _kpi.Connections;

    /// <summary>Gets the aggregated node count without its unit label.</summary>
    public string NodeCountValueText => _kpi.Nodes;

    /// <summary>Gets the aggregated profile count without its unit label.</summary>
    public string ProfileCountValueText => _kpi.Profiles;

    /// <summary>Gets the node-health record caption below the node count.</summary>
    public string NodeHealthText => _kpi.NodeHealth;

    /// <summary>Gets the recent-traffic chart title.</summary>
    public string DailyChartTitleText => _localization.GetString("Statistics.Chart.Title");

    /// <summary>Gets the empty-window message for the recent-traffic chart.</summary>
    public string NoDailyChartText => _localization.GetString("Statistics.Chart.Empty");

    /// <summary>Gets one chronological column per calendar day in the chart window, including empty days.</summary>
    public IReadOnlyList<StatisticsDailyBarDisplay> DailyChartBars => _dailyChartBars;

    /// <summary>Gets the localized first and last day of the chart window.</summary>
    public string DailyChartRangeText => _dailyChartRangeText;

    /// <summary>Gets the localized busiest-day total used as the chart's only value annotation.</summary>
    public string DailyChartPeakText => _dailyChartPeakText;

    /// <summary>Gets whether any day in the chart window has recorded traffic.</summary>
    public bool HasDailyChartData => HasSnapshot && _dailyChartBars.Any(static bar => bar.Fraction > 0);

    /// <summary>Gets whether a loaded chart window should show its empty message instead of columns.</summary>
    public bool HasNoDailyChartData => HasSnapshot && !IsLoading && !HasLoadError && !HasDailyChartData;

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
        _lastSnapshot = null;
        _lastUpdated = null;
        _totalTrafficText = _connectionCountText = _profileStatisticText = _snapshotStatisticText = _nodeStatisticText = _ruleStatisticText = string.Empty;
        _profileTrafficRows = [];
        _dailyTrafficRows = [];
        _nodeTrafficRows = [];
        _ruleHitRows = [];
        _kpi = StatisticsKpiDisplay.Empty;
        _dailyChartBars = [];
        _dailyChartRangeText = _dailyChartPeakText = string.Empty;
        foreach (string property in new[] { nameof(TotalTrafficText), nameof(ConnectionCountText), nameof(ProfileStatisticText),
            nameof(SnapshotStatisticText), nameof(NodeStatisticText), nameof(RuleStatisticText), nameof(ProfileTrafficRows),
            nameof(DailyTrafficRows), nameof(NodeTrafficRows), nameof(RuleHitRows), nameof(HasSnapshot), nameof(StatusText) })
        {
            OnPropertyChanged(property);
        }
        NotifyPresentationProjections();
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

    private void ApplyLoadSnapshot(StatisticsLoadSnapshot snapshot) => ApplyLoadSnapshot(snapshot, recordObservation: true);

    private void ApplyLoadSnapshot(StatisticsLoadSnapshot snapshot, bool recordObservation)
    {
        StatisticsLoadSnapshot cached = recordObservation
            ? snapshot with
            {
                ProfileTrafficRows = snapshot.ProfileTrafficRows.ToArray(),
                DailyTrafficRows = snapshot.DailyTrafficRows.ToArray(),
                NodeTrafficRows = snapshot.NodeTrafficRows.ToArray(),
                RuleHitCounts = snapshot.RuleHitCounts.ToArray(),
                ProfileNames = new Dictionary<string, string>(snapshot.ProfileNames, StringComparer.Ordinal),
            } : snapshot;
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
        long maximumHits = snapshot.RuleHitCounts.Count == 0 ? 0 : snapshot.RuleHitCounts.Max(static row => row.Value);
        StatisticsRuleHitDisplay[] ruleRows = snapshot.RuleHitCounts.Select(row => new StatisticsRuleHitDisplay(
            _displayMapper.MapText(row.Key), row.Value,
            string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.RuleHits.Format"), row.Value),
            ToShare(row.Value, maximumHits))).ToArray();
        DateTimeOffset? updated = recordObservation ? _getNow() : _lastUpdated;
        StatisticsKpiDisplay kpi = new(
            FormatByteCount((decimal)summary.TotalUploadBytes + summary.TotalDownloadBytes),
            summary.ConnectionCount.ToString("N0", CultureInfo.CurrentCulture),
            summary.NodeCount.ToString("N0", CultureInfo.CurrentCulture),
            summary.ProfileCount.ToString("N0", CultureInfo.CurrentCulture),
            string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Kpi.NodeHealth.Format"), summary.NodeHealthCount));
        DateOnly chartEnd = DateOnly.FromDateTime((updated ?? _getNow()).ToLocalTime().DateTime);
        (IReadOnlyList<StatisticsDailyBarDisplay> chartBars, decimal chartPeakBytes) = FormatDailyChart(snapshot.DailyTrafficRows, chartEnd);
        string chartRange = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Chart.Range.Format"),
            chartBars[0].DateLabel, chartBars[^1].DateLabel);
        string chartPeak = string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Chart.Peak.Format"),
            FormatByteCount(chartPeakBytes));

        // Prepare every value first, then publish one complete snapshot to binding observers.
        if (recordObservation) { _lastSnapshot = cached; }
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
        _kpi = kpi;
        _dailyChartBars = chartBars;
        _dailyChartRangeText = chartRange;
        _dailyChartPeakText = chartPeak;
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
        NotifyPresentationProjections();
        NotifyEmptyStates();
    }

    private void NotifyEmptyStates()
    {
        OnPropertyChanged(nameof(HasNoProfileTraffic));
        OnPropertyChanged(nameof(HasNoDailyTraffic));
        OnPropertyChanged(nameof(HasNoNodeTraffic));
        OnPropertyChanged(nameof(HasNoRuleHits));
        OnPropertyChanged(nameof(HasDailyChartData));
        OnPropertyChanged(nameof(HasNoDailyChartData));
    }

    /// <summary>Publishes the stat-tile and chart projections that are derived from one installed snapshot.</summary>
    private void NotifyPresentationProjections()
    {
        OnPropertyChanged(nameof(TotalTransferValueText));
        OnPropertyChanged(nameof(ConnectionCountValueText));
        OnPropertyChanged(nameof(NodeCountValueText));
        OnPropertyChanged(nameof(ProfileCountValueText));
        OnPropertyChanged(nameof(NodeHealthText));
        OnPropertyChanged(nameof(DailyChartBars));
        OnPropertyChanged(nameof(DailyChartRangeText));
        OnPropertyChanged(nameof(DailyChartPeakText));
    }

    /// <summary>Scales a non-negative value against the largest value, keeping non-zero values visible.</summary>
    private static double ToShare(decimal value, decimal maximum)
    {
        if (value <= 0 || maximum <= 0)
        {
            return 0;
        }

        return Math.Clamp((double)(value / maximum), MinimumVisibleShare, 1);
    }

    /// <summary>Builds one chronological column per day ending at <paramref name="last"/>; missing days are zero.</summary>
    /// <returns>The columns and the busiest day's byte total inside the window.</returns>
    private (IReadOnlyList<StatisticsDailyBarDisplay> Bars, decimal PeakBytes) FormatDailyChart(
        IReadOnlyList<TrafficStatisticRow> rows,
        DateOnly last)
    {
        Dictionary<DateOnly, decimal> uploads = [];
        Dictionary<DateOnly, decimal> downloads = [];
        foreach (TrafficStatisticRow row in rows)
        {
            if (DateOnly.TryParseExact(row.Label, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly day))
            {
                uploads[day] = row.UploadBytes;
                downloads[day] = row.DownloadBytes;
            }
        }

        DateOnly first = last.AddDays(1 - DailyChartDays);
        decimal peak = 0;
        for (DateOnly day = first; day <= last; day = day.AddDays(1))
        {
            peak = Math.Max(peak, uploads.GetValueOrDefault(day) + downloads.GetValueOrDefault(day));
        }

        string barFormat = _localization.GetString("Statistics.Chart.Bar.Format");
        string totalFormat = _localization.GetString("Statistics.Total.Format");
        string transferFormat = _localization.GetString("Statistics.TotalTraffic.Format");
        List<StatisticsDailyBarDisplay> bars = new(DailyChartDays);
        for (DateOnly day = first; day <= last; day = day.AddDays(1))
        {
            decimal upload = uploads.GetValueOrDefault(day);
            decimal download = downloads.GetValueOrDefault(day);
            string dateLabel = day.ToString("d", CultureInfo.CurrentCulture);
            string total = string.Format(CultureInfo.CurrentCulture, totalFormat, FormatByteCount(upload + download));
            string transfer = string.Format(CultureInfo.CurrentCulture, transferFormat, FormatByteCount(upload), FormatByteCount(download));
            bars.Add(new StatisticsDailyBarDisplay(
                day,
                day.Day.ToString(CultureInfo.CurrentCulture),
                dateLabel,
                total,
                transfer,
                ToShare(upload + download, peak),
                string.Format(CultureInfo.CurrentCulture, barFormat, dateLabel, total, transfer)));
        }

        return (bars, peak);
    }

    /// <summary>Formats a byte count for compact UI display.</summary>
    /// <param name="bytes">Byte count.</param>
    /// <returns>Formatted byte count.</returns>
    private string FormatByteCount(decimal bytes) => _localization.Formatter.Bytes(bytes, "N1");

    private IReadOnlyList<StatisticsTrafficRowDisplay> FormatTrafficRows(
        IReadOnlyList<TrafficStatisticRow> rows,
        string countFormat,
        IReadOnlyDictionary<string, string>? profileNames = null,
        bool daily = false)
    {
        List<StatisticsTrafficRowDisplay> resolvedRows = new(rows.Count);
        decimal maximum = rows.Count == 0 ? 0 : rows.Max(static row => (decimal)row.UploadBytes + row.DownloadBytes);
        foreach (TrafficStatisticRow row in rows)
        {
            string rawLabel = profileNames is not null && profileNames.TryGetValue(row.Label, out string? profileName)
                ? profileName
                : row.Label;
            if (profileNames is not null && StringComparer.Ordinal.Equals(row.Label, ProfileIdentifiers.BuiltInDirect))
            { rawLabel = _localization.GetString("ProfileCatalog.BuiltInDirect.Name"); }
            string label = daily && DateOnly.TryParseExact(rawLabel, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out DateOnly date)
                ? date.ToString("d", CultureInfo.CurrentCulture)
                : _displayMapper.MapText(rawLabel);
            resolvedRows.Add(new StatisticsTrafficRowDisplay(row, label,
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Total.Format"), FormatByteCount((decimal)row.UploadBytes + row.DownloadBytes)),
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.TotalTraffic.Format"), FormatByteCount(row.UploadBytes), FormatByteCount(row.DownloadBytes)),
                string.Format(CultureInfo.CurrentCulture, _localization.GetString(countFormat), row.SampleCount),
                string.Format(CultureInfo.CurrentCulture, _localization.GetString("Statistics.Updated.Format"),
                    _localization.Formatter.Timestamp(row.UpdatedAt)),
                ToShare((decimal)row.UploadBytes + row.DownloadBytes, maximum)));
        }

        return resolvedRows;
    }

    /// <summary>Headline stat-tile values prepared together so one snapshot never mixes tile generations.</summary>
    private sealed record StatisticsKpiDisplay(
        string TotalTransfer,
        string Connections,
        string Nodes,
        string Profiles,
        string NodeHealth)
    {
        public static StatisticsKpiDisplay Empty { get; } = new(string.Empty, string.Empty, string.Empty, string.Empty, string.Empty);
    }

    private sealed record StatisticsLoadSnapshot(
        StatisticsSummary Summary,
        IReadOnlyList<TrafficStatisticRow> ProfileTrafficRows,
        IReadOnlyList<TrafficStatisticRow> DailyTrafficRows,
        IReadOnlyList<TrafficStatisticRow> NodeTrafficRows,
        IReadOnlyList<KeyValuePair<string, long>> RuleHitCounts,
        IReadOnlyDictionary<string, string> ProfileNames);
}
