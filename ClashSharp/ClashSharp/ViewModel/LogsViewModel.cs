using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for log storage display and cleanup.</summary>
/// <remarks>
/// Invariants: Storage usage and recent logs are non-null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding.
/// Side effects: Cleanup methods mutate persistent log storage.
/// </remarks>
internal sealed class LogsViewModel : ObservableObject
{
    private const int VisibleLogLimit = 1000;

    /// <summary>Localization resolver used by visible labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Log storage boundary used by this view model.</summary>
    private readonly ILogManagementStore _logStorage;

    /// <summary>Reports unexpected log interaction failures.</summary>
    private readonly IApplicationErrorSink _errorSink;

    /// <summary>Backing field for <see cref="StorageUsageText"/>.</summary>
    private string _storageUsageText = string.Empty;

    /// <summary>Backing field for <see cref="RecentLogs"/>.</summary>
    private IReadOnlyList<LogRecord> _recentLogs = [];

    private IReadOnlyList<string> _categoryFilterOptions = [];

    private IReadOnlyList<string> _levelFilterOptions = [];

    private readonly Dictionary<string, string?> _levelFilterValues = new(StringComparer.Ordinal);

    private readonly Dictionary<string, string?> _categoryFilterValues = new(StringComparer.Ordinal);

    private string _selectedLevelFilter = string.Empty;

    private string _selectedCategoryFilter = string.Empty;

    private string _searchText = string.Empty;

    private string? _requestedSourceFilter;

    /// <summary>Initializes a logs view model.</summary>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    /// <param name="logStorage">Log storage service. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public LogsViewModel(
        Func<string, string> getString,
        ILogManagementStore logStorage,
        IApplicationErrorSink errorSink)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => StringComparer.Ordinal.Equals(CurrentCategoryFilter, "Trigger")
        ? _getString("Triggers.Logs.Title")
        : _getString("Nav.Logs");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => StringComparer.Ordinal.Equals(CurrentCategoryFilter, "Trigger")
        ? _getString("Triggers.Logs.Description")
        : _getString("Page.Logs.Description");

    /// <summary>Gets the storage card title.</summary>
    /// <value>Localized card title.</value>
    public string StorageTitleText => _getString("Logs.Storage.Title");

    /// <summary>Gets the cleanup command label.</summary>
    /// <value>Localized command label.</value>
    public string CleanupText => _getString("Command.Cleanup");

    /// <summary>Gets the stable text displayed before an asynchronous cleanup preview is available.</summary>
    /// <value>Localized zero-impact preview text.</value>
    public string CleanupPreviewPlaceholderText => FormatCleanupPreview(default);

    public string RefreshText => _getString("Command.Refresh");

    public string SearchPlaceholderText => _getString("Logs.Filter.SearchPlaceholder");

    public string SearchLabelText => MatchLocalized("搜索", "搜尋", "Search", "Поиск", "Rechercher", "Suchen");

    public string LevelFilterLabelText => _getString("Logs.Filter.Level");

    public string CategoryFilterLabelText => _getString("Logs.Filter.Category");

    public string TimeColumnText => _getString("Logs.Column.Time");

    public string LevelColumnText => _getString("Logs.Column.Level");

    public string CategoryColumnText => _getString("Logs.Column.Category");

    public string ContentColumnText => _getString("Logs.Column.Content");

    public string EmptyText => _getString("Logs.Empty");

    public string SearchText
    {
        get => _searchText;
        set => ApplySearchText(value);
    }

    public IReadOnlyList<string> LevelFilterOptions
    {
        get => _levelFilterOptions;
        private set => SetProperty(ref _levelFilterOptions, value);
    }

    public IReadOnlyList<string> CategoryFilterOptions
    {
        get => _categoryFilterOptions;
        private set => SetProperty(ref _categoryFilterOptions, value);
    }

    public string SelectedLevelFilter
    {
        get => _selectedLevelFilter;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? AllLevelsText : value.Trim();
            SetProperty(ref _selectedLevelFilter, normalized);
        }
    }

    public string SelectedCategoryFilter
    {
        get => _selectedCategoryFilter;
        set
        {
            string normalized = string.IsNullOrWhiteSpace(value) ? AllCategoriesText : value.Trim();
            if (SetProperty(ref _selectedCategoryFilter, normalized))
            {
                _requestedSourceFilter = null;
                OnPropertyChanged(nameof(PageTitleText));
                OnPropertyChanged(nameof(DescriptionText));
            }
        }
    }

    /// <summary>Gets storage usage summary text.</summary>
    /// <value>Formatted storage summary.</value>
    public string StorageUsageText
    {
        get => _storageUsageText;
        private set => SetProperty(ref _storageUsageText, value);
    }

    /// <summary>Gets recent log records.</summary>
    /// <value>Recent logs; never null.</value>
    public IReadOnlyList<LogRecordDisplay> RecentLogs
    {
        get => _recentLogs.Select(CreateDisplayRow).ToList();
        private set => SetProperty(ref _recentLogs, value.Select(static row => row.Record).ToList());
    }

    /// <summary>Loads log storage state without blocking the UI thread.</summary>
    /// <param name="cancellationToken">Cancels this page-load attempt.</param>
    /// <returns>A task that completes after the snapshot is applied or the failure is reported.</returns>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        LogLoadRequest request = CaptureLoadRequest();
        return ViewModelLoadExecutor.ExecuteAsync(
            () => ReadLoadSnapshot(request),
            ApplyLoadSnapshot,
            _errorSink,
            "logs-load",
            cancellationToken);
    }

    private LogLoadRequest CaptureLoadRequest()
    {
        return new LogLoadRequest(
            CurrentCategoryFilter,
            EffectiveLevelFilter,
            _searchText);
    }

    private LogLoadSnapshot ReadLoadSnapshot(LogLoadRequest request)
    {
        return new LogLoadSnapshot(
            _logStorage.GetStorageSummary(),
            _logStorage.GetLogSources(),
            _logStorage.GetLogs(
                VisibleLogLimit,
                request.CategoryFilter,
                request.LevelFilter,
                request.SearchText),
            request.CategoryFilter);
    }

    private void ApplyLoadSnapshot(LogLoadSnapshot snapshot)
    {
        LogStorageSnapshot summary = snapshot.Summary;
        StorageUsageText = string.Format(
            CultureInfo.CurrentCulture,
            _getString("Logs.StorageUsage.Format"),
            FormatByteCount(summary.DatabaseSizeBytes),
            summary.LogCount,
            summary.ConnectionCount);
        RefreshLevelFilterOptions();
        RefreshCategoryFilterOptions(snapshot.Sources, snapshot.CategoryFilter);
        _recentLogs = snapshot.Logs;
        OnPropertyChanged(nameof(RecentLogs));
    }

    public void ApplySearchText(string? searchText)
    {
        string normalized = string.IsNullOrWhiteSpace(searchText) ? string.Empty : searchText.Trim();
        SetProperty(ref _searchText, normalized, nameof(SearchText));
    }

    public void SetSourceFilter(string? source)
    {
        _requestedSourceFilter = string.IsNullOrWhiteSpace(source) ? null : source.Trim();
        SetProperty(ref _selectedCategoryFilter, string.Empty, nameof(SelectedCategoryFilter));
        OnPropertyChanged(nameof(PageTitleText));
        OnPropertyChanged(nameof(DescriptionText));
    }

    /// <summary>Applies a cleanup mode away from the UI thread and refreshes visible storage state.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterValue">Numeric cleanup parameter value.</param>
    /// <param name="levelFilter">Selected localized level filter, or null.</param>
    /// <param name="categoryFilter">Selected localized category filter, or null.</param>
    /// <param name="cancellationToken">Cancels queued work and prevents stale snapshot application.</param>
    /// <returns>A task that completes after cleanup and snapshot application or error reporting.</returns>
    /// <remarks>
    /// Cancellation cannot roll back a SQLite mutation that has already started. A canceled caller
    /// never receives a stale post-cleanup snapshot; its next page load observes the durable result.
    /// </remarks>
    public Task ApplyCleanupModeAsync(
        int selectedIndex,
        double parameterValue,
        string? levelFilter,
        string? categoryFilter,
        CancellationToken cancellationToken)
    {
        string? resolvedLevelFilter = ResolveLevelFilter(levelFilter);
        string? resolvedCategoryFilter = ResolveCategoryFilter(categoryFilter);
        LogLoadRequest loadRequest = CaptureLoadRequest();
        return ViewModelLoadExecutor.ExecuteAsync(
            () =>
            {
                ApplyCleanupModeCore(
                    selectedIndex,
                    parameterValue,
                    resolvedLevelFilter,
                    resolvedCategoryFilter);
                cancellationToken.ThrowIfCancellationRequested();
                return ReadLoadSnapshot(loadRequest);
            },
            ApplyLoadSnapshot,
            _errorSink,
            "logs-cleanup",
            cancellationToken);
    }

    /// <summary>Reads and formats a cleanup preview away from the UI thread.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterValue">Numeric cleanup parameter value.</param>
    /// <param name="levelFilter">Selected localized level filter, or null.</param>
    /// <param name="categoryFilter">Selected localized category filter, or null.</param>
    /// <param name="cancellationToken">Cancels queued work and prevents stale text application.</param>
    /// <returns>
    /// Localized preview text, or null when a non-fatal storage failure was reported and existing UI
    /// text should remain unchanged.
    /// </returns>
    public async Task<string?> GetCleanupPreviewTextAsync(
        int selectedIndex,
        double parameterValue,
        string? levelFilter,
        string? categoryFilter,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (selectedIndex is not (3 or 4))
        {
            return CleanupPreviewPlaceholderText;
        }

        string? resolvedLevelFilter = ResolveLevelFilter(levelFilter);
        string? resolvedCategoryFilter = ResolveCategoryFilter(categoryFilter);
        LogCleanupEstimate? preview = null;
        await ViewModelLoadExecutor.ExecuteAsync(
            () => ReadCleanupPreview(
                selectedIndex,
                parameterValue,
                resolvedLevelFilter,
                resolvedCategoryFilter),
            result => preview = result,
            _errorSink,
            "logs-cleanup-preview",
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return preview is LogCleanupEstimate estimate
            ? FormatCleanupPreview(estimate)
            : null;
    }

    private void ApplyCleanupModeCore(
        int selectedIndex,
        double parameterValue,
        string? levelFilter,
        string? categoryFilter)
    {
        switch (selectedIndex)
        {
            case 0:
                int keepDays = CoercePositiveInteger(parameterValue, 30);
                _logStorage.CleanupBefore(DateTimeOffset.UtcNow.AddDays(-keepDays));
                break;
            case 1:
                long targetSizeBytes = CoercePositiveInteger(parameterValue, 10) * 1024L * 1024L;
                _logStorage.CleanupToSize(targetSizeBytes);
                break;
            case 2:
                _logStorage.CleanupToLogCount(CoercePositiveInteger(parameterValue, 1000));
                break;
            case 3:
                _logStorage.ClearAll();
                break;
            case 4:
                _logStorage.CleanupLogs(levelFilter, categoryFilter);
                break;
        }
    }

    private LogCleanupEstimate ReadCleanupPreview(
        int selectedIndex,
        double parameterValue,
        string? levelFilter,
        string? categoryFilter)
    {
        _ = parameterValue;
        return selectedIndex switch
        {
            3 => ReadClearAllPreview(),
            4 => _logStorage.PreviewLogCleanup(levelFilter, categoryFilter),
            _ => default,
        };
    }

    private LogCleanupEstimate ReadClearAllPreview()
    {
        LogStorageSnapshot summary = _logStorage.GetStorageSummary();
        return new LogCleanupEstimate(
            summary.LogCount + summary.ConnectionCount,
            summary.DatabaseSizeBytes);
    }

    private string FormatCleanupPreview(LogCleanupEstimate preview)
    {
        return string.Format(
            CultureInfo.CurrentCulture,
            MatchLocalized(
                "将清理 {0:N0} 个条目 / 约 {1}",
                "將清理 {0:N0} 個項目 / 約 {1}",
                "Will clean {0:N0} entries / about {1}",
                "Будет очищено {0:N0} записей / около {1}",
                "Nettoiera {0:N0} entrees / environ {1}",
                "Bereinigt {0:N0} Eintraege / ca. {1}"),
            preview.EntryCount,
            FormatByteCount(preview.EstimatedSizeBytes));
    }

    /// <summary>Converts a number-box value to a positive integer with fallback.</summary>
    /// <param name="value">Number-box value.</param>
    /// <param name="fallback">Fallback value used for invalid input.</param>
    /// <returns>Positive integer value.</returns>
    private static int CoercePositiveInteger(double value, int fallback)
    {
        if (double.IsNaN(value) || value <= 0)
        {
            return fallback;
        }

        return Math.Max(1, (int)Math.Round(value));
    }

    /// <summary>Formats a byte count for compact storage display.</summary>
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

        return $"{value:N2} {units[unitIndex]}";
    }

    private string AllLevelsText => _getString("Logs.Filter.AllLevels");

    private string AllCategoriesText => _getString("Logs.Filter.AllCategories");

    private string? EffectiveLevelFilter => _levelFilterValues.TryGetValue(SelectedLevelFilter, out string? value)
        ? value
        : null;

    private string? EffectiveCategoryFilter => _categoryFilterValues.TryGetValue(SelectedCategoryFilter, out string? value)
        ? value
        : null;

    private string? CurrentCategoryFilter => _requestedSourceFilter ?? EffectiveCategoryFilter;

    private string? ResolveLevelFilter(string? displayValue)
    {
        return displayValue is not null && _levelFilterValues.TryGetValue(displayValue, out string? value)
            ? value
            : null;
    }

    private string? ResolveCategoryFilter(string? displayValue)
    {
        return displayValue is not null && _categoryFilterValues.TryGetValue(displayValue, out string? value)
            ? value
            : null;
    }

    private void RefreshLevelFilterOptions()
    {
        List<string> options =
        [
            AllLevelsText,
            FormatLogLevel("Info"),
            FormatLogLevel("Warning"),
            FormatLogLevel("Error"),
        ];

        _levelFilterValues.Clear();
        _levelFilterValues[options[0]] = null;
        _levelFilterValues[options[1]] = "Info";
        _levelFilterValues[options[2]] = "Warning";
        _levelFilterValues[options[3]] = "Error";
        LevelFilterOptions = options;
        if (!options.Contains(SelectedLevelFilter, StringComparer.Ordinal))
        {
            SetProperty(ref _selectedLevelFilter, AllLevelsText, nameof(SelectedLevelFilter));
        }
    }

    private void RefreshCategoryFilterOptions(
        IReadOnlyList<string> sources,
        string? selectedSource)
    {
        List<string> options = [AllCategoriesText];
        _categoryFilterValues.Clear();
        _categoryFilterValues[AllCategoriesText] = null;
        foreach (string source in sources)
        {
            string display = FormatLogCategory(source);
            if (!options.Contains(display, StringComparer.Ordinal))
            {
                options.Add(display);
            }

            _categoryFilterValues[display] = source;
        }

        string selectedDisplay = AllCategoriesText;
        if (selectedSource is not null)
        {
            selectedDisplay = FormatLogCategory(selectedSource);
            if (!options.Contains(selectedDisplay, StringComparer.Ordinal))
            {
                options.Add(selectedDisplay);
                _categoryFilterValues[selectedDisplay] = selectedSource;
            }
        }

        CategoryFilterOptions = options;
        SetProperty(
            ref _selectedCategoryFilter,
            selectedDisplay,
            nameof(SelectedCategoryFilter));
    }

    private LogRecordDisplay CreateDisplayRow(LogRecord record)
    {
        return new LogRecordDisplay(record, FormatLogLevel(record.Level), FormatLogCategory(record.Source));
    }

    private string FormatLogLevel(string level)
    {
        return level switch
        {
            "Info" => MatchLocalized(
                simplifiedChinese: "信息",
                traditionalChinese: "資訊",
                english: "Info",
                russian: "Информация",
                french: "Info",
                german: "Info"),
            "Warning" => MatchLocalized(
                simplifiedChinese: "警告",
                traditionalChinese: "警告",
                english: "Warning",
                russian: "Предупреждение",
                french: "Avertissement",
                german: "Warnung"),
            "Error" => MatchLocalized(
                simplifiedChinese: "错误",
                traditionalChinese: "錯誤",
                english: "Error",
                russian: "Ошибка",
                french: "Erreur",
                german: "Fehler"),
            _ => level,
        };
    }

    private string FormatLogCategory(string source)
    {
        return source switch
        {
            "Settings" => _getString("Nav.Settings"),
            "Trigger" => _getString("Nav.Triggers"),
            "Notification" => _getString("Settings.Section.Notification"),
            "Profiles" => _getString("Nav.Profiles"),
            "Links" => _getString("Nav.Links"),
            "Connections" => _getString("Nav.Connections"),
            "MihomoService" => _getString("About.Mihomo.Title"),
            "StartupRestoreFallback" => _getString("Settings.StartupRestoreFallback.Title"),
            _ => source,
        };
    }

    private string MatchLocalized(string simplifiedChinese, string traditionalChinese, string english, string russian, string french, string german)
    {
        string allLevels = AllLevelsText;
        if (StringComparer.Ordinal.Equals(allLevels, "全部级别"))
        {
            return simplifiedChinese;
        }

        if (StringComparer.Ordinal.Equals(allLevels, "全部級別"))
        {
            return traditionalChinese;
        }

        if (StringComparer.Ordinal.Equals(allLevels, "Все уровни"))
        {
            return russian;
        }

        if (StringComparer.Ordinal.Equals(allLevels, "Tous les niveaux"))
        {
            return french;
        }

        if (StringComparer.Ordinal.Equals(allLevels, "Alle Ebenen"))
        {
            return german;
        }

        return english;
    }

    private sealed record LogLoadRequest(
        string? CategoryFilter,
        string? LevelFilter,
        string SearchText);

    private sealed record LogLoadSnapshot(
        LogStorageSnapshot Summary,
        IReadOnlyList<string> Sources,
        IReadOnlyList<LogRecord> Logs,
        string? CategoryFilter);
}
