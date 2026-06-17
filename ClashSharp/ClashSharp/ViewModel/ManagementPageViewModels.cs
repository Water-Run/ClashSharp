/*
 * Management Page ViewModels
 * Provides MVVM state for profiles, links, and logs management pages
 *
 * @author: WaterRun
 * @file: ViewModel/ManagementPageViewModels.cs
 * @date: 2026-06-17
 */

#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.ViewModel;

/// <summary>Bindable view model for profile management.</summary>
/// <remarks>
/// Invariants: Profile rows and active profile text are non-null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding and command execution.
/// Side effects: Commands can import, validate, activate profiles, and write logs through injected delegates.
/// </remarks>
internal sealed class ProfilesViewModel : ObservableObject
{
    /// <summary>Localization resolver used by visible labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Profile catalog service used by this view model.</summary>
    private readonly ProfileCatalogService _profiles;

    /// <summary>Log service used by this view model.</summary>
    private readonly LogStorageService _log;

    /// <summary>Backing field for <see cref="Profiles"/>.</summary>
    private IReadOnlyList<ConfigurationProfile> _profilesRows = [];

    /// <summary>Backing field for <see cref="SelectedProfile"/>.</summary>
    private ConfigurationProfile? _selectedProfile;

    /// <summary>Backing field for <see cref="ActiveProfileText"/>.</summary>
    private string _activeProfileText = string.Empty;

    /// <summary>Initializes a profiles view model.</summary>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    /// <param name="profiles">Profile catalog service. Must not be null.</param>
    /// <param name="log">Log service. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ProfilesViewModel(Func<string, string> getString, ProfileCatalogService profiles, LogStorageService log)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        ValidateProfileCommand = new AsyncRelayCommand(ValidateSelectedProfileAsync);
        SetActiveProfileCommand = new RelayCommand(SetSelectedProfileActive);
        RefreshProfiles();
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _getString("Nav.Profiles");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _getString("Page.Profiles.Description");

    /// <summary>Gets the import command label.</summary>
    /// <value>Localized command label.</value>
    public string ImportProfileText => _getString("Command.Import");

    /// <summary>Gets the validate command label.</summary>
    /// <value>Localized command label.</value>
    public string ValidateProfileText => _getString("Command.Validate");

    /// <summary>Gets the set-active command label.</summary>
    /// <value>Localized command label.</value>
    public string SetActiveProfileText => _getString("Command.SetActive");

    /// <summary>Gets the current-profile label.</summary>
    /// <value>Localized label text.</value>
    public string CurrentProfileTitleText => _getString("Label.CurrentProfile");

    /// <summary>Gets profile rows.</summary>
    /// <value>Profile rows; never null.</value>
    public IReadOnlyList<ConfigurationProfile> Profiles
    {
        get => _profilesRows;
        private set => SetProperty(ref _profilesRows, value);
    }

    /// <summary>Gets or sets the selected profile.</summary>
    /// <value>Selected profile, or null when no profile is selected.</value>
    public ConfigurationProfile? SelectedProfile
    {
        get => _selectedProfile;
        set => SetProperty(ref _selectedProfile, value);
    }

    /// <summary>Gets the active profile status text.</summary>
    /// <value>Active profile display text; never null.</value>
    public string ActiveProfileText
    {
        get => _activeProfileText;
        private set => SetProperty(ref _activeProfileText, value);
    }

    /// <summary>Gets the command that validates the selected profile.</summary>
    /// <value>Asynchronous validate command.</value>
    public AsyncRelayCommand ValidateProfileCommand { get; }

    /// <summary>Gets the command that activates the selected profile.</summary>
    /// <value>Synchronous activation command.</value>
    public RelayCommand SetActiveProfileCommand { get; }

    /// <summary>Refreshes profile rows and active-profile text.</summary>
    public void RefreshProfiles()
    {
        IReadOnlyList<ConfigurationProfile> profiles = _profiles.GetProfiles();
        Profiles = profiles;
        ActiveProfileText = ResolveActiveProfileDisplayText(profiles);
    }

    /// <summary>Imports a local profile file and refreshes profile rows.</summary>
    /// <param name="filePath">Local profile file path. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the import when requested.</param>
    /// <returns>A task that completes after import handling finishes.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the profile catalog service.
    /// Thread / reentrancy: The caller owns file picker serialization.
    /// </remarks>
    public async Task ImportLocalProfileAsync(string filePath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        try
        {
            ProfileImportResult result = await _profiles.ImportLocalProfileAsync(filePath, cancellationToken);
            _log.AppendLog("Info", "Profiles", $"Local profile imported: {result.ProfileName}.", result.ConfigPath);
            RefreshProfiles();
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or IOException or InvalidOperationException or OperationCanceledException)
        {
            _log.AppendLog("Warning", "Profiles", "Local profile import failed.", exception.Message);
        }
    }

    /// <summary>Validates the selected profile and refreshes profile rows.</summary>
    /// <param name="cancellationToken">Cancels validation when requested.</param>
    /// <returns>A task that completes after validation handling finishes.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the profile catalog service.
    /// Thread / reentrancy: UI callers should use <see cref="ValidateProfileCommand"/>.
    /// </remarks>
    public async Task ValidateSelectedProfileAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is not ConfigurationProfile profile)
        {
            _log.AppendLog("Info", "Profiles", "No profile selected.", null);
            return;
        }

        try
        {
            ProfileImportResult result = await _profiles.ValidateProfileAsync(profile, cancellationToken);
            _log.AppendLog("Info", "Profiles", $"Profile validation completed: {profile.Name}.", result.ConfigPath);
        }
        catch (Exception exception) when (exception is ArgumentException or FileNotFoundException or InvalidOperationException or OperationCanceledException)
        {
            _log.AppendLog("Warning", "Profiles", "Profile validation failed.", exception.Message);
        }

        RefreshProfiles();
    }

    /// <summary>Sets the selected profile as active when possible.</summary>
    public void SetSelectedProfileActive()
    {
        if (SelectedProfile is not ConfigurationProfile profile)
        {
            _log.AppendLog("Info", "Profiles", "No profile selected.", null);
            return;
        }

        if (_profiles.TrySetActiveProfile(profile.Id))
        {
            _log.AppendLog("Info", "Profiles", $"Active profile changed to {profile.Name}.", profile.Id);
            RefreshProfiles();
        }
    }

    /// <summary>Resolves active profile display text from current rows.</summary>
    /// <param name="profiles">Current profile rows. Must not be null.</param>
    /// <returns>Active profile display text.</returns>
    private string ResolveActiveProfileDisplayText(IEnumerable<ConfigurationProfile> profiles)
    {
        foreach (ConfigurationProfile profile in profiles)
        {
            if (profile.IsActive)
            {
                return $"{profile.NameDisplay} - {profile.StatusDisplay}";
            }
        }

        return AppSettingsService.Instance.ActiveProfileId;
    }
}

/// <summary>Bindable view model for subscription link management.</summary>
/// <remarks>
/// Invariants: Link rows are non-null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding and command execution.
/// Side effects: Commands can add, check, import subscription links, and write logs.
/// </remarks>
internal sealed class LinksViewModel : ObservableObject
{
    /// <summary>Localization resolver used by visible labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Profile catalog service used by link operations.</summary>
    private readonly ProfileCatalogService _profiles;

    /// <summary>Log service used by link operations.</summary>
    private readonly LogStorageService _log;

    /// <summary>Backing field for <see cref="SubscriptionLinks"/>.</summary>
    private IReadOnlyList<ProfileSubscriptionLink> _subscriptionLinks = [];

    /// <summary>Backing field for <see cref="SelectedLink"/>.</summary>
    private ProfileSubscriptionLink? _selectedLink;

    /// <summary>Initializes a links view model.</summary>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    /// <param name="profiles">Profile catalog service. Must not be null.</param>
    /// <param name="log">Log service. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public LinksViewModel(Func<string, string> getString, ProfileCatalogService profiles, LogStorageService log)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        CheckLinkCommand = new AsyncRelayCommand(CheckSelectedLinkAsync);
        UpdateLinkCommand = new AsyncRelayCommand(UpdateSelectedLinkAsync);
        RefreshLinks();
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _getString("Nav.Links");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _getString("Page.Links.Description");

    /// <summary>Gets the add command label.</summary>
    /// <value>Localized command label.</value>
    public string AddLinkText => _getString("Command.Add");

    /// <summary>Gets the check command label.</summary>
    /// <value>Localized command label.</value>
    public string CheckLinksText => _getString("Command.Check");

    /// <summary>Gets the update command label.</summary>
    /// <value>Localized command label.</value>
    public string UpdateLinksText => _getString("Command.Update");

    /// <summary>Gets subscription link rows.</summary>
    /// <value>Subscription link rows; never null.</value>
    public IReadOnlyList<ProfileSubscriptionLink> SubscriptionLinks
    {
        get => _subscriptionLinks;
        private set => SetProperty(ref _subscriptionLinks, value);
    }

    /// <summary>Gets or sets the selected subscription link.</summary>
    /// <value>Selected link, or null when none is selected.</value>
    public ProfileSubscriptionLink? SelectedLink
    {
        get => _selectedLink;
        set => SetProperty(ref _selectedLink, value);
    }

    /// <summary>Gets the command that checks the selected link.</summary>
    /// <value>Asynchronous check command.</value>
    public AsyncRelayCommand CheckLinkCommand { get; }

    /// <summary>Gets the command that imports the selected link.</summary>
    /// <value>Asynchronous update command.</value>
    public AsyncRelayCommand UpdateLinkCommand { get; }

    /// <summary>Refreshes visible subscription links.</summary>
    public void RefreshLinks()
    {
        SubscriptionLinks = _profiles.GetSubscriptionLinks();
    }

    /// <summary>Adds a subscription link and refreshes visible rows.</summary>
    /// <param name="name">Link name. Must not be null.</param>
    /// <param name="uri">Subscription URI. Must not be null.</param>
    public void AddLink(string name, string uri)
    {
        try
        {
            ProfileSubscriptionLink link = _profiles.AddSubscriptionLink(name, uri);
            _log.AppendLog("Info", "Links", $"Subscription link added: {link.Name}.", link.Uri);
            RefreshLinks();
        }
        catch (ArgumentException exception)
        {
            _log.AppendLog("Warning", "Links", "Subscription link could not be added.", exception.Message);
        }
    }

    /// <summary>Checks the selected subscription link.</summary>
    /// <param name="cancellationToken">Cancels the check when requested.</param>
    /// <returns>A task that completes after check handling finishes.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the profile catalog service.
    /// Thread / reentrancy: UI callers should use <see cref="CheckLinkCommand"/>.
    /// </remarks>
    public async Task CheckSelectedLinkAsync(CancellationToken cancellationToken)
    {
        if (SelectedLink is not ProfileSubscriptionLink link)
        {
            _log.AppendLog("Info", "Links", "No subscription link selected.", null);
            return;
        }

        try
        {
            string status = await _profiles.CheckSubscriptionLinkAsync(link, cancellationToken);
            _log.AppendLog("Info", "Links", $"Subscription link check completed: {status}.", link.Name);
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            _log.AppendLog("Warning", "Links", "Subscription link check failed.", exception.Message);
        }

        RefreshLinks();
    }

    /// <summary>Imports the selected subscription link as a profile.</summary>
    /// <param name="cancellationToken">Cancels the import when requested.</param>
    /// <returns>A task that completes after import handling finishes.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the profile catalog service.
    /// Thread / reentrancy: UI callers should use <see cref="UpdateLinkCommand"/>.
    /// </remarks>
    public async Task UpdateSelectedLinkAsync(CancellationToken cancellationToken)
    {
        if (SelectedLink is not ProfileSubscriptionLink link)
        {
            _log.AppendLog("Info", "Links", "No subscription link selected.", null);
            return;
        }

        try
        {
            ProfileImportResult result = await _profiles.ImportSubscriptionLinkAsync(link, cancellationToken);
            _log.AppendLog("Info", "Links", $"Subscription profile imported: {result.ProfileName}.", result.ConfigPath);
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or OperationCanceledException or InvalidOperationException or IOException)
        {
            _log.AppendLog("Warning", "Links", "Subscription profile import failed.", exception.Message);
        }

        RefreshLinks();
    }
}

/// <summary>Bindable view model for log storage display and cleanup.</summary>
/// <remarks>
/// Invariants: Storage usage and recent logs are non-null after construction.
/// Thread safety: Not thread-safe; intended for UI-thread binding.
/// Side effects: Cleanup methods mutate persistent log storage.
/// </remarks>
internal sealed class LogsViewModel : ObservableObject
{
    /// <summary>Localization resolver used by visible labels.</summary>
    private readonly Func<string, string> _getString;

    /// <summary>Log storage service used by this view model.</summary>
    private readonly LogStorageService _logStorage;

    /// <summary>Backing field for <see cref="StorageUsageText"/>.</summary>
    private string _storageUsageText = string.Empty;

    /// <summary>Backing field for <see cref="RecentLogs"/>.</summary>
    private IReadOnlyList<LogRecord> _recentLogs = [];

    /// <summary>Initializes a logs view model.</summary>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    /// <param name="logStorage">Log storage service. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public LogsViewModel(Func<string, string> getString, LogStorageService logStorage)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _logStorage = logStorage ?? throw new ArgumentNullException(nameof(logStorage));
        RefreshLogs();
    }

    /// <summary>Gets the page title text.</summary>
    /// <value>Localized page title.</value>
    public string PageTitleText => _getString("Nav.Logs");

    /// <summary>Gets the page description text.</summary>
    /// <value>Localized page description.</value>
    public string DescriptionText => _getString("Page.Logs.Description");

    /// <summary>Gets the storage card title.</summary>
    /// <value>Localized card title.</value>
    public string StorageTitleText => _getString("Logs.Storage.Title");

    /// <summary>Gets the cleanup command label.</summary>
    /// <value>Localized command label.</value>
    public string CleanupText => _getString("Command.Cleanup");

    /// <summary>Gets storage usage summary text.</summary>
    /// <value>Formatted storage summary.</value>
    public string StorageUsageText
    {
        get => _storageUsageText;
        private set => SetProperty(ref _storageUsageText, value);
    }

    /// <summary>Gets recent log records.</summary>
    /// <value>Recent logs; never null.</value>
    public IReadOnlyList<LogRecord> RecentLogs
    {
        get => _recentLogs;
        private set => SetProperty(ref _recentLogs, value);
    }

    /// <summary>Refreshes storage usage and recent log rows.</summary>
    public void RefreshLogs()
    {
        LogStorageSummary summary = _logStorage.GetStorageSummary();
        StorageUsageText = string.Format(
            _getString("Logs.StorageUsage.Format"),
            FormatByteCount(summary.DatabaseSizeBytes),
            summary.LogCount,
            summary.ConnectionCount);
        RecentLogs = _logStorage.GetRecentLogs(100);
    }

    /// <summary>Applies a cleanup mode and refreshes visible log storage state.</summary>
    /// <param name="selectedIndex">Selected cleanup mode index.</param>
    /// <param name="parameterValue">Numeric cleanup parameter value.</param>
    public void ApplyCleanupMode(int selectedIndex, double parameterValue)
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
        }

        RefreshLogs();
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
}
