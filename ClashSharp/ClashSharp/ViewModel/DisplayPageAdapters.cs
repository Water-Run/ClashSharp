/*
 * Display Page Adapters
 * Connects read-oriented display page view models to application services
 *
 * @author: WaterRun
 * @file: ViewModel/DisplayPageAdapters.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using Windows.System;

namespace ClashSharp.ViewModel;

/// <summary>Adapts <see cref="LocalizationService"/> to display page localization.</summary>
/// <remarks>
/// Invariants: Wraps a non-null localization service.
/// Thread safety: Matches the wrapped service and is intended for UI-thread use.
/// Side effects: Reads localized resource strings.
/// </remarks>
internal sealed class DisplayPageLocalizationAdapter : IDisplayPageLocalization
{
    /// <summary>Wrapped localization service.</summary>
    private readonly LocalizationService _localization;

    /// <summary>Initializes a display page localization adapter.</summary>
    /// <param name="localization">Localization service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="localization"/> is null.</exception>
    public DisplayPageLocalizationAdapter(LocalizationService localization)
    {
        _localization = localization ?? throw new ArgumentNullException(nameof(localization));
    }

    /// <summary>Gets a localized string for the supplied key.</summary>
    /// <param name="key">Localization key. Must not be null.</param>
    /// <returns>Localized string or fallback text.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="key"/> is null.</exception>
    public string GetString(string key)
    {
        return _localization.GetString(key);
    }
}

/// <summary>Adapts <see cref="RuleCatalogService"/> to rule catalog reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null rule catalog service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads active profile rule metadata.
/// </remarks>
internal sealed class RuleCatalogAdapter : IRuleCatalog
{
    /// <summary>Wrapped rule catalog service.</summary>
    private readonly RuleCatalogService _rules;

    /// <summary>Initializes a rule catalog adapter.</summary>
    /// <param name="rules">Rule catalog service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="rules"/> is null.</exception>
    public RuleCatalogAdapter(RuleCatalogService rules)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
    }

    /// <summary>Gets visible rule preview rows.</summary>
    /// <returns>Read-only rule preview rows.</returns>
    public IReadOnlyList<RulePreview> GetRules()
    {
        return _rules.GetRules();
    }
}

/// <summary>Adapts <see cref="LogStorageService"/> to statistics reads.</summary>
/// <remarks>
/// Invariants: Wraps a non-null log storage service.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads persistent statistics storage.
/// </remarks>
internal sealed class StatisticsStoreAdapter : IStatisticsStore
{
    /// <summary>Wrapped log storage service.</summary>
    private readonly LogStorageService _logStorage;

    /// <summary>Initializes a statistics store adapter.</summary>
    /// <param name="logStorage">Log storage service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logStorage"/> is null.</exception>
    public StatisticsStoreAdapter(LogStorageService logStorage)
    {
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
    }

    /// <summary>Gets the current aggregate statistics summary.</summary>
    /// <returns>Current aggregate statistics summary.</returns>
    public StatisticsSummary GetTrafficStatisticsSummary()
    {
        TrafficStatisticsSummary summary = _logStorage.GetTrafficStatisticsSummary();
        return new StatisticsSummary(
            summary.TotalUploadBytes,
            summary.TotalDownloadBytes,
            summary.ConnectionCount,
            summary.SnapshotCount,
            summary.ProfileCount,
            summary.NodeCount,
            summary.NodeHealthCount,
            summary.RuleCount);
    }

    /// <summary>Gets profile traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Profile traffic rows.</returns>
    public IReadOnlyList<TrafficStatisticRow> GetProfileTrafficRows(int limit)
    {
        return _logStorage.GetProfileTrafficRows(limit);
    }

    /// <summary>Gets daily traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Daily traffic rows.</returns>
    public IReadOnlyList<TrafficStatisticRow> GetDailyTrafficRows(int limit)
    {
        return _logStorage.GetDailyTrafficRows(limit);
    }

    /// <summary>Gets node traffic rows.</summary>
    /// <param name="limit">Maximum number of rows; must be greater than zero.</param>
    /// <returns>Node traffic rows.</returns>
    public IReadOnlyList<TrafficStatisticRow> GetNodeTrafficRows(int limit)
    {
        return _logStorage.GetNodeTrafficRows(limit);
    }
}

/// <summary>Adapts <see cref="ProfileCatalogService"/> to profile-name lookup.</summary>
/// <remarks>
/// Invariants: Returns profile display names keyed by profile identifiers.
/// Thread safety: Matches the wrapped service.
/// Side effects: Reads profile catalog metadata.
/// </remarks>
internal sealed class StatisticsProfilesAdapter : IStatisticsProfiles
{
    /// <summary>Wrapped profile catalog service.</summary>
    private readonly ProfileCatalogService _profiles;

    /// <summary>Initializes a statistics profiles adapter.</summary>
    /// <param name="profiles">Profile catalog service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="profiles"/> is null.</exception>
    public StatisticsProfilesAdapter(ProfileCatalogService profiles)
    {
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
    }

    /// <summary>Gets profile display names keyed by profile identifier.</summary>
    /// <returns>Profile display names keyed by identifier.</returns>
    public IReadOnlyDictionary<string, string> GetProfileDisplayNamesById()
    {
        Dictionary<string, string> names = new(StringComparer.Ordinal);
        foreach (ConfigurationProfile profile in _profiles.GetProfiles())
        {
            names[profile.Id] = profile.NameDisplay;
        }

        return names;
    }
}

/// <summary>Adapts <see cref="MihomoCoreService"/> to about-page core probing.</summary>
/// <remarks>
/// Invariants: Wraps a non-null core service.
/// Thread safety: Matches the wrapped service.
/// Side effects: May start a short-lived version probe process.
/// </remarks>
internal sealed class AboutCoreAdapter : IAboutCore
{
    /// <summary>Wrapped core service.</summary>
    private readonly MihomoCoreService _core;

    /// <summary>Initializes an about core adapter.</summary>
    /// <param name="core">Core service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is null.</exception>
    public AboutCoreAdapter(MihomoCoreService core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    /// <summary>Gets bundled core version text.</summary>
    /// <param name="cancellationToken">Cancels the version probe when requested.</param>
    /// <returns>Version text.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the wrapped service.
    /// Completion semantics: Does not mutate long-running core state.
    /// </remarks>
    public Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
    {
        return _core.GetVersionTextAsync(cancellationToken);
    }
}

/// <summary>Adapts Windows URI launching to the about page.</summary>
/// <remarks>
/// Invariants: Launch requests are delegated to the platform launcher.
/// Thread safety: Intended for UI-thread use.
/// Side effects: Opens an external URI through Windows.
/// </remarks>
internal sealed class WindowsUriLauncher : IUriLauncher
{
    /// <summary>Launches the supplied URI.</summary>
    /// <param name="uri">URI to launch. Must not be null.</param>
    /// <param name="cancellationToken">Cancellation token accepted for command consistency.</param>
    /// <returns>A task that completes after the platform launch request completes.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="uri"/> is null.</exception>
    /// <remarks>
    /// Cancellation semantics: The platform launcher does not expose cancellation; canceled tokens are ignored.
    /// Completion semantics: Completion does not guarantee the external application remains open.
    /// </remarks>
    public async Task LaunchAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        await Launcher.LaunchUriAsync(uri);
    }
}
