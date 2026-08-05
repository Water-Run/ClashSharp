using System;
using System.Collections.Generic;
using System.IO;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

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

    /// <summary>Profile catalog boundary used by this view model.</summary>
    private readonly IProfileManagementCatalog _profiles;

    /// <summary>Presentation log boundary used by this view model.</summary>
    private readonly IPageLog _log;

    /// <summary>Resolves the persisted active profile when no catalog row is active.</summary>
    private readonly Func<string> _getActiveProfileId;

    /// <summary>Reports unexpected asynchronous presentation failures.</summary>
    private readonly IApplicationErrorSink _errorSink;

    /// <summary>Maps persisted profile models to UI-only display rows.</summary>
    private readonly IModelDisplayMapper _displayMapper;

    /// <summary>Backing field for <see cref="Profiles"/>.</summary>
    private IReadOnlyList<ConfigurationProfileDisplay> _profilesRows = [];

    /// <summary>Backing field for <see cref="SelectedProfile"/>.</summary>
    private ConfigurationProfileDisplay? _selectedProfile;

    /// <summary>Backing field for <see cref="ActiveProfileText"/>.</summary>
    private string _activeProfileText = string.Empty;

    /// <summary>Initializes a profiles view model.</summary>
    /// <param name="getString">Localization resolver. Must not be null.</param>
    /// <param name="profiles">Profile catalog service. Must not be null.</param>
    /// <param name="log">Log service. Must not be null.</param>
    /// <param name="getActiveProfileId">Active-profile resolver. Must not be null.</param>
    /// <param name="errorSink">Unexpected error sink. Must not be null.</param>
    /// <param name="displayMapper">UI display row mapper. Must not be null.</param>
    /// <exception cref="ArgumentNullException">A required dependency is null.</exception>
    public ProfilesViewModel(
        Func<string, string> getString,
        IProfileManagementCatalog profiles,
        IPageLog log,
        Func<string> getActiveProfileId,
        IApplicationErrorSink errorSink,
        IModelDisplayMapper displayMapper)
    {
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _profiles = profiles ?? throw new ArgumentNullException(nameof(profiles));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _getActiveProfileId = getActiveProfileId
            ?? throw new ArgumentNullException(nameof(getActiveProfileId));
        _errorSink = errorSink ?? throw new ArgumentNullException(nameof(errorSink));
        _displayMapper = displayMapper ?? throw new ArgumentNullException(nameof(displayMapper));
        ImportProfileCommand = new AsyncRelayCommand(
            ImportLocalProfileFromCommandAsync,
            _errorSink,
            operationName: "profiles-import");
        ValidateProfileCommand = new AsyncRelayCommand(
            ValidateSelectedProfileAsync,
            _errorSink,
            operationName: "profiles-validate");
        SetActiveProfileCommand = new AsyncRelayCommand(
            SetSelectedProfileActiveAsync,
            _errorSink,
            operationName: "profiles-set-active");
        RenameProfileCommand = new AsyncRelayCommand(
            RenameProfileFromCommandAsync,
            _errorSink,
            operationName: "profiles-rename");
        DeleteProfileCommand = new AsyncRelayCommand(
            DeleteProfileFromCommandAsync,
            _errorSink,
            operationName: "profiles-delete");
        RollbackProfileCommand = new AsyncRelayCommand(
            RollbackProfileFromCommandAsync,
            _errorSink,
            operationName: "profiles-rollback");
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

    /// <summary>Gets the rename command label.</summary>
    public string RenameProfileText => _getString("Command.Rename");

    /// <summary>Gets the delete command label.</summary>
    public string DeleteProfileText => _getString("Command.Delete");

    /// <summary>Gets the retained-history command label.</summary>
    public string ProfileHistoryText => _getString("Command.History");

    /// <summary>Gets the current-profile label.</summary>
    /// <value>Localized label text.</value>
    public string CurrentProfileTitleText => _getString("Label.CurrentProfile");

    /// <summary>Gets profile rows.</summary>
    /// <value>Profile rows; never null.</value>
    public IReadOnlyList<ConfigurationProfileDisplay> Profiles
    {
        get => _profilesRows;
        private set => SetProperty(ref _profilesRows, value);
    }

    /// <summary>Gets or sets the selected profile.</summary>
    /// <value>Selected profile, or null when no profile is selected.</value>
    public ConfigurationProfileDisplay? SelectedProfile
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

    /// <summary>Gets the command that imports a file selected by the page.</summary>
    /// <value>Asynchronous import command.</value>
    public AsyncRelayCommand ImportProfileCommand { get; }

    /// <summary>Gets the command that validates the selected profile.</summary>
    /// <value>Asynchronous validate command.</value>
    public AsyncRelayCommand ValidateProfileCommand { get; }

    /// <summary>Gets the command that activates the selected profile.</summary>
    /// <value>Asynchronous activation command.</value>
    public AsyncRelayCommand SetActiveProfileCommand { get; }

    /// <summary>Gets the command that renames a user profile.</summary>
    public AsyncRelayCommand RenameProfileCommand { get; }

    /// <summary>Gets the command that deletes a user profile.</summary>
    public AsyncRelayCommand DeleteProfileCommand { get; }

    /// <summary>Gets the command that restores a retained profile version.</summary>
    public AsyncRelayCommand RollbackProfileCommand { get; }

    /// <summary>Loads the current profile catalog without blocking the UI thread.</summary>
    /// <param name="cancellationToken">Cancels this page-load attempt.</param>
    /// <returns>A task that completes after the snapshot is applied or the failure is reported.</returns>
    public Task LoadAsync(CancellationToken cancellationToken)
    {
        return ViewModelLoadExecutor.ExecuteAsync(
            ReadLoadSnapshot,
            ApplyLoadSnapshot,
            _errorSink,
            "profiles-load",
            cancellationToken);
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
            cancellationToken.ThrowIfCancellationRequested();
            _log.Append("Info", "Profiles", $"Local profile imported: {result.ProfileName}.", result.ConfigPath);
            await LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FileNotFoundException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Profiles", "Local profile import failed.", exception.Message);
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
        if (SelectedProfile is not ConfigurationProfileDisplay selectedProfile)
        {
            _log.Append("Info", "Profiles", "No profile selected.", null);
            return;
        }

        ConfigurationProfile profile = selectedProfile.Model;
        try
        {
            ProfileImportResult result = await _profiles.ValidateProfileAsync(profile, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _log.Append("Info", "Profiles", $"Profile validation completed: {profile.Name}.", result.ConfigPath);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or FileNotFoundException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Profiles", "Profile validation failed.", exception.Message);
        }

        await LoadAsync(cancellationToken);
    }

    /// <summary>Sets the selected profile as active when possible.</summary>
    /// <param name="cancellationToken">Cancels activation and the following reload.</param>
    /// <returns>A task that completes after activation and reloading finish.</returns>
    public async Task SetSelectedProfileActiveAsync(CancellationToken cancellationToken)
    {
        if (SelectedProfile is not ConfigurationProfileDisplay selectedProfile)
        {
            _log.Append("Info", "Profiles", "No profile selected.", null);
            return;
        }

        ConfigurationProfile profile = selectedProfile.Model;
        try
        {
            bool activated = await _profiles.TrySetActiveProfileAsync(
                profile.Id,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!activated)
            {
                return;
            }

            _log.Append("Info", "Profiles", $"Active profile changed to {profile.Name}.", profile.Id);
            await LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Profiles", "Active profile could not be changed.", exception.Message);
        }
    }

    /// <summary>Returns retained versions for one profile, or an empty snapshot when history cannot be read.</summary>
    public IReadOnlyList<ProfileHistoryEntry> GetProfileHistory(string profileId)
    {
        try
        {
            return _profiles.GetProfileHistory(profileId);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            _log.Append("Warning", "Profiles", "Profile history could not be loaded.", exception.Message);
            return [];
        }
    }

    /// <summary>Renames one user profile and refreshes visible rows.</summary>
    public async Task RenameProfileAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken)
    {
        try
        {
            bool renamed = await _profiles
                .TryRenameProfileAsync(profileId, name, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (renamed)
            {
                _log.Append("Info", "Profiles", "Profile renamed.", profileId);
                await LoadAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Profiles", "Profile could not be renamed.", exception.Message);
        }
    }

    /// <summary>Deletes one user profile and refreshes visible rows.</summary>
    public async Task DeleteProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        try
        {
            bool deleted = await _profiles.TryDeleteProfileAsync(profileId, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (deleted)
            {
                SelectedProfile = null;
                _log.Append("Info", "Profiles", "Profile deleted.", profileId);
                await LoadAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or AggregateException
                or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Profiles", "Profile could not be deleted.", exception.Message);
        }
    }

    /// <summary>Restores one retained profile version and refreshes visible rows.</summary>
    public async Task RollbackProfileAsync(
        ProfileHistoryEntry historyEntry,
        CancellationToken cancellationToken)
    {
        try
        {
            ProfileImportResult result = await _profiles
                .RollbackProfileAsync(historyEntry, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _log.Append("Info", "Profiles", "Profile history version restored.", result.ConfigPath);
            await LoadAsync(cancellationToken);
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or InvalidOperationException
                or AggregateException
                or OperationCanceledException
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            _log.Append("Warning", "Profiles", "Profile history version could not be restored.", exception.Message);
        }
    }

    /// <summary>Resolves active profile display text from current rows.</summary>
    /// <param name="profiles">Current profile rows. Must not be null.</param>
    /// <returns>Active profile display text.</returns>
    private static string ResolveActiveProfileDisplayText(
        IEnumerable<ConfigurationProfileDisplay> profiles,
        string fallbackProfileId)
    {
        foreach (ConfigurationProfileDisplay profile in profiles)
        {
            if (profile.IsActive)
            {
                return $"{profile.NameDisplay} - {profile.StatusDisplay}";
            }
        }

        return fallbackProfileId;
    }

    private ProfileLoadSnapshot ReadLoadSnapshot()
    {
        IReadOnlyList<ConfigurationProfile> profiles = _profiles.GetProfiles();
        return new ProfileLoadSnapshot(
            profiles,
            _getActiveProfileId());
    }

    private void ApplyLoadSnapshot(ProfileLoadSnapshot snapshot)
    {
        ApplyProfiles(snapshot.Profiles, snapshot.FallbackProfileId);
    }

    private void ApplyProfiles(
        IReadOnlyList<ConfigurationProfile> profiles,
        string fallbackProfileId)
    {
        List<ConfigurationProfileDisplay> rows = new(profiles.Count);
        foreach (ConfigurationProfile profile in profiles)
        {
            rows.Add(_displayMapper.Map(profile));
        }

        Profiles = rows;
        ActiveProfileText = ResolveActiveProfileDisplayText(rows, fallbackProfileId);
    }

    private Task ImportLocalProfileFromCommandAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        if (parameter is not string filePath)
        {
            throw new ArgumentException(
                "The profile import command requires a file path.",
                nameof(parameter));
        }

        return ImportLocalProfileAsync(filePath, cancellationToken);
    }

    private Task RenameProfileFromCommandAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        if (parameter is not ValueTuple<string, string> renameInput)
        {
            throw new ArgumentException(
                "The profile rename command requires a profile identifier and name.",
                nameof(parameter));
        }

        return RenameProfileAsync(renameInput.Item1, renameInput.Item2, cancellationToken);
    }

    private Task DeleteProfileFromCommandAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        if (parameter is not string profileId)
        {
            throw new ArgumentException(
                "The profile delete command requires a profile identifier.",
                nameof(parameter));
        }

        return DeleteProfileAsync(profileId, cancellationToken);
    }

    private Task RollbackProfileFromCommandAsync(
        object? parameter,
        CancellationToken cancellationToken)
    {
        if (parameter is not ProfileHistoryEntry historyEntry)
        {
            throw new ArgumentException(
                "The profile rollback command requires a history entry.",
                nameof(parameter));
        }

        return RollbackProfileAsync(historyEntry, cancellationToken);
    }

    private sealed record ProfileLoadSnapshot(
        IReadOnlyList<ConfigurationProfile> Profiles,
        string FallbackProfileId);
}
