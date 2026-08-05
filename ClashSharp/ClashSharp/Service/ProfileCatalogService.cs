using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides active profile settings for profile catalog rows.</summary>
internal interface IProfileCatalogSettings
{
    /// <summary>Gets or sets the active profile identifier.</summary>
    string ActiveProfileId { get; set; }
}

internal interface IProfileCatalogAdmittedSettings
{
    void SetActiveProfileAdmitted(
        MutationAdmissionLease admissionLease,
        string profileId);
}

/// <summary>Imports, validates, and ensures profile configuration files.</summary>
internal interface IProfileCatalogCoreConfiguration
{
    /// <summary>Imports a downloaded or local configuration profile.</summary>
    Task<ProfileImportResult> ImportProfileConfigurationAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken);

    /// <summary>Reads the currently committed source for one imported profile.</summary>
    Task<string?> ReadImportedProfileConfigurationAsync(
        string profileId,
        CancellationToken cancellationToken);

    /// <summary>Ensures the built-in default configuration exists.</summary>
    CoreConfigurationState EnsureDefaultConfiguration();

    /// <summary>Validates an already imported profile.</summary>
    Task<ProfileImportResult> ValidateImportedProfileAsync(string profileId, CancellationToken cancellationToken);
}

/// <summary>Persists profile catalog warning logs.</summary>
internal interface IProfileCatalogLog
{
    /// <summary>Appends a profile catalog log entry.</summary>
    void AppendLog(string level, string category, string message, string? detail);
}

/// <summary>Applies explicit profile candidates through the sole live runtime owner.</summary>
internal interface IProfileCatalogRuntime
{
    Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken);

    Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(
        string profileId,
        string profileName,
        string configurationText,
        CancellationToken cancellationToken);

    Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken);
}

/// <summary>Combined active-profile source import and live-apply outcome.</summary>
internal readonly record struct ProfileCatalogRuntimeImportResult(
    ProfileImportResult Profile,
    bool IsApplied);

/// <summary>Localized fallback text captured before background profile-catalog aggregation.</summary>
internal readonly record struct ProfileCatalogFallbackStrings(
    string BuiltInProfileName,
    string AvailableStatus);

/// <summary>Profile and subscription counts normalized with production catalog semantics.</summary>
internal readonly record struct ProfileCatalogSummary(int ProfileCount, int SubscriptionCount);

/// <summary>Provides local configuration profile and subscription-link data for WinUI pages.</summary>
/// <remarks>
/// Invariants: At least one built-in profile is always available.
/// Thread safety: Public members serialize mutable state through a private lock.
/// Side effects: Reads and writes the local profile catalog JSON file; persists active profile selection to application settings.
/// </remarks>
public sealed partial class ProfileCatalogService
{
    /// <summary>Synchronization object guarding active profile mutations for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Absolute path to the profile catalog JSON file.</summary>
    private readonly string _catalogPath;

    /// <summary>Absolute root containing immutable profile configuration versions.</summary>
    private readonly string _historyRoot;

    /// <summary>Cached catalog document loaded from disk during this service lifetime.</summary>
    private ProfileCatalogDocument? _cachedDocument;

    /// <summary>Per-link single-flight gates spanning download through durable catalog commit.</summary>
    private readonly Dictionary<string, SemaphoreSlim> _subscriptionUpdateGates = new(StringComparer.Ordinal);

    /// <summary>Serializes live profile activation, active updates, rollback, and deletion.</summary>
    private readonly SemaphoreSlim _profileMutationGate = new(1, 1);

    private readonly IProfileCatalogSettings _settings;

    private readonly IProfileCatalogAdmittedSettings? _admittedSettings;

    private readonly IProfileCatalogCoreConfiguration _coreConfiguration;

    private readonly IProfileCatalogLog _log;

    private readonly IProfileCatalogRuntime _runtime;

    private readonly IProfileCatalogMutationCoordinator _mutationCoordinator;

    private readonly Func<string, string> _getString;

    /// <summary>Obsolete preview profile identifier removed from early catalog builds.</summary>
    private const string ObsoleteSampleProfileId = "sample-rule-profile";

    /// <summary>Maximum accepted local or subscription profile size in bytes.</summary>
    private const int MaxProfileConfigurationBytes = 4 * 1024 * 1024;

    private const int MaxSubscriptionDownloadBytes = MaxProfileConfigurationBytes;

    /// <summary>Maximum retained successful versions for each profile.</summary>
    private const int MaxProfileHistoryEntriesPerProfile = 20;

    /// <summary>Minimum accepted automatic subscription update interval.</summary>
    private const int MinSubscriptionUpdateIntervalHours = 1;

    /// <summary>Maximum accepted automatic subscription update interval.</summary>
    private const int MaxSubscriptionUpdateIntervalHours = 24 * 365;

    private static readonly TimeSpan MinimumSubscriptionRetryDelay = TimeSpan.FromMinutes(5);

    private static readonly TimeSpan MaximumSubscriptionRetryDelay = TimeSpan.FromHours(6);

    /// <summary>Shared HTTP client used for subscription checks and downloads.</summary>
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>Shared serializer settings for human-readable catalog persistence.</summary>
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);

    /// <summary>Initializes the profile catalog service.</summary>
    internal ProfileCatalogService(
        string catalogPath,
        string historyRoot,
        IProfileCatalogSettings settings,
        IProfileCatalogCoreConfiguration coreConfiguration,
        IProfileCatalogRuntime runtime,
        IProfileCatalogLog log,
        Func<string, string> getString,
        IProfileCatalogMutationCoordinator mutationCoordinator)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(catalogPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyRoot);

        _catalogPath = Path.GetFullPath(catalogPath);
        _historyRoot = Path.GetFullPath(historyRoot);
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _admittedSettings = settings as IProfileCatalogAdmittedSettings;
        _coreConfiguration = coreConfiguration ?? throw new ArgumentNullException(nameof(coreConfiguration));
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
        _mutationCoordinator = mutationCoordinator
            ?? throw new ArgumentNullException(nameof(mutationCoordinator));
    }

    /// <summary>Returns all known configuration profiles with active-profile state applied.</summary>
    /// <returns>A read-only snapshot of known configuration profiles.</returns>
    public IReadOnlyList<ConfigurationProfile> GetProfiles()
    {
        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument();
            string activeProfileId = GetActiveProfileId();
            List<ConfigurationProfile> profiles = [];

            foreach (ConfigurationProfile profile in document.Profiles)
            {
                profiles.Add(profile with { IsActive = StringComparer.Ordinal.Equals(profile.Id, activeProfileId) });
            }

            return profiles;
        }
    }

    /// <summary>Returns all known subscription links.</summary>
    /// <returns>A read-only snapshot of known subscription links.</returns>
    public IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks()
    {
        lock (_syncLock)
        {
            return [.. LoadDocument().Links];
        }
    }

    /// <summary>Returns retained versions for one profile, newest first.</summary>
    public IReadOnlyList<ProfileHistoryEntry> GetProfileHistory(string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        lock (_syncLock)
        {
            return [.. LoadDocument().History
                .FindAll(entry => StringComparer.Ordinal.Equals(entry.ProfileId, profileId))
                .OrderByDescending(entry => entry.CreatedAt)];
        }
    }

    /// <summary>Returns enabled subscription links whose update interval has elapsed.</summary>
    public IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now)
    {
        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument();
            List<ProfileSubscriptionLink> dueLinks = [];
            foreach (ProfileSubscriptionLink link in document.Links)
            {
                DateTimeOffset nextAttemptAt = FindScheduleState(document, link.Id)?.NextAttemptAt
                    ?? GetInitialNextAttemptAt(link, now);
                if (link.IsEnabled && nextAttemptAt <= now)
                {
                    dueLinks.Add(link);
                }
            }

            return dueLinks;
        }
    }

    /// <summary>Persists scheduler success or bounded retry backoff independently from visible attempt timestamps.</summary>
    private void RecordSubscriptionUpdateOutcome(
        string linkId,
        bool succeeded,
        DateTimeOffset attemptedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        lock (_syncLock)
        {
            ProfileCatalogDocument document = CloneDocument(LoadDocument());
            RecordSubscriptionUpdateOutcome(document, linkId, succeeded, attemptedAt);
            SaveDocument(document);
        }
    }

    internal Task RecordSubscriptionUpdateOutcomeAsync(
        string linkId,
        bool succeeded,
        DateTimeOffset attemptedAt,
        CancellationToken cancellationToken)
    {
        return _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                RecordSubscriptionUpdateOutcome(linkId, succeeded, attemptedAt);
                return Task.FromResult(true);
            },
            cancellationToken);
    }

    /// <summary>
    /// Returns normalized catalog counts without invoking this service's localization dependency.
    /// </summary>
    /// <remarks>
    /// Thread safety: Serialized by the catalog lock. The supplied strings must be captured on the
    /// localization-owning thread before this method is dispatched to a worker.
    /// </remarks>
    internal ProfileCatalogSummary GetSummary(ProfileCatalogFallbackStrings fallbackStrings)
    {
        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument(key => key switch
            {
                "ProfileCatalog.BuiltInDirect.Name" => fallbackStrings.BuiltInProfileName,
                "ProfileCatalog.Status.Available" => fallbackStrings.AvailableStatus,
                _ => key,
            });
            return new ProfileCatalogSummary(document.Profiles.Count, document.Links.Count);
        }
    }

    /// <summary>Adds a subscription link to the local profile catalog.</summary>
    /// <param name="name">User-facing link name. Must not be null or whitespace.</param>
    /// <param name="uri">Subscription URI. Must not be null, whitespace, or invalid.</param>
    /// <returns>The added subscription link.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="uri"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is whitespace or <paramref name="uri"/> is invalid.</exception>
    private ProfileSubscriptionLink AddSubscriptionLinkCore(string name, string uri)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(uri);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Subscription link name must not be whitespace.", nameof(name));
        }

        Uri parsedUri = ParseSubscriptionUri(uri);

        lock (_syncLock)
        {
            ProfileCatalogDocument document = CloneDocument(LoadDocument());
            foreach (ProfileSubscriptionLink existingLink in document.Links)
            {
                if (StringComparer.OrdinalIgnoreCase.Equals(existingLink.Uri, parsedUri.ToString()))
                {
                    throw new ArgumentException("Subscription link URI already exists.", nameof(uri));
                }
            }

            ProfileSubscriptionLink link = new(
                Guid.NewGuid().ToString("N"),
                name.Trim(),
                parsedUri.ToString(),
                true,
                24,
                DateTimeOffset.MinValue,
                GetString("ProfileCatalog.Status.Added"),
                Revision: 1);

            document.Links.Add(link);
            document.SubscriptionSchedules.Add(new ProfileSubscriptionScheduleState
            {
                LinkId = link.Id,
                NextAttemptAt = DateTimeOffset.Now,
                ConsecutiveFailures = 0,
                LastSuccessfulUpdateAt = null,
                LastAttemptAt = null,
            });
            SaveDocument(document);
            return link;
        }
    }

    /// <summary>Adds a subscription link inside process-wide mutation admission.</summary>
    public Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(
        string name,
        string uri,
        CancellationToken cancellationToken)
    {
        return _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(AddSubscriptionLinkCore(name, uri));
            },
            cancellationToken);
    }

    /// <summary>Updates editable subscription properties while retaining status and timestamps.</summary>
    private bool TryUpdateSubscriptionLinkCore(
        string linkId,
        string name,
        string uri,
        bool isEnabled,
        int updateIntervalHours)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Uri parsedUri = ParseSubscriptionUri(uri);
        if (updateIntervalHours is < MinSubscriptionUpdateIntervalHours or > MaxSubscriptionUpdateIntervalHours)
        {
            throw new ArgumentOutOfRangeException(
                nameof(updateIntervalHours),
                updateIntervalHours,
                $"Subscription update interval must be between {MinSubscriptionUpdateIntervalHours} and {MaxSubscriptionUpdateIntervalHours} hours.");
        }

        lock (_syncLock)
        {
            ProfileCatalogDocument document = CloneDocument(LoadDocument());
            int targetIndex = document.Links.FindIndex(link => StringComparer.Ordinal.Equals(link.Id, linkId));
            if (targetIndex < 0)
            {
                return false;
            }

            foreach (ProfileSubscriptionLink link in document.Links)
            {
                if (!StringComparer.Ordinal.Equals(link.Id, linkId)
                    && StringComparer.OrdinalIgnoreCase.Equals(link.Uri, parsedUri.ToString()))
                {
                    throw new ArgumentException("Subscription link URI already exists.", nameof(uri));
                }
            }

            ProfileSubscriptionLink existingLink = document.Links[targetIndex];
            bool uriChanged = !StringComparer.OrdinalIgnoreCase.Equals(
                existingLink.Uri,
                parsedUri.ToString());
            document.Links[targetIndex] = existingLink with
            {
                Name = name.Trim(),
                Uri = parsedUri.ToString(),
                IsEnabled = isEnabled,
                UpdateIntervalHours = updateIntervalHours,
                Revision = checked(existingLink.Revision + 1),
            };
            ProfileSubscriptionScheduleState schedule = GetOrCreateScheduleState(document, existingLink);
            if (uriChanged)
            {
                schedule.NextAttemptAt = DateTimeOffset.Now;
                schedule.ConsecutiveFailures = 0;
                schedule.LastSuccessfulUpdateAt = null;
                schedule.LastAttemptAt = null;
            }
            else if (isEnabled)
            {
                schedule.NextAttemptAt = schedule.LastSuccessfulUpdateAt is { } lastSuccessful
                    ? lastSuccessful.AddHours(updateIntervalHours)
                    : DateTimeOffset.Now;
            }
            SaveDocument(document);
            return true;
        }
    }

    /// <summary>Serializes subscription edits against manual and scheduled imports for the same link.</summary>
    internal async Task<bool> TryUpdateSubscriptionLinkAsync(
        string linkId,
        string name,
        string uri,
        bool isEnabled,
        int updateIntervalHours,
        CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            async (_, token) =>
            {
                SemaphoreSlim updateGate = GetSubscriptionUpdateGate(linkId);
                await updateGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return TryUpdateSubscriptionLinkCore(
                        linkId,
                        name,
                        uri,
                        isEnabled,
                        updateIntervalHours);
                }
                finally
                {
                    updateGate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes one subscription link from the catalog.</summary>
    private bool TryDeleteSubscriptionLinkCore(string linkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);

        lock (_syncLock)
        {
            ProfileCatalogDocument document = CloneDocument(LoadDocument());
            int removed = document.Links.RemoveAll(link => StringComparer.Ordinal.Equals(link.Id, linkId));
            if (removed == 0)
            {
                return false;
            }

            document.SubscriptionSchedules.RemoveAll(state => StringComparer.Ordinal.Equals(state.LinkId, linkId));
            SaveDocument(document);
            return true;
        }
    }

    /// <summary>Serializes subscription deletion against manual and scheduled imports for the same link.</summary>
    internal async Task<bool> TryDeleteSubscriptionLinkAsync(
        string linkId,
        CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            async (_, token) =>
            {
                SemaphoreSlim updateGate = GetSubscriptionUpdateGate(linkId);
                await updateGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return TryDeleteSubscriptionLinkCore(linkId);
                }
                finally
                {
                    updateGate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Renames one user profile.</summary>
    private bool TryRenameProfileCore(string profileId, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (StringComparer.Ordinal.Equals(profileId, ProfileCatalogIds.BuiltInDirect))
        {
            return false;
        }

        lock (_syncLock)
        {
            ProfileCatalogDocument document = CloneDocument(LoadDocument());
            for (int index = 0; index < document.Profiles.Count; index++)
            {
                ConfigurationProfile profile = document.Profiles[index];
                if (!StringComparer.Ordinal.Equals(profile.Id, profileId))
                {
                    continue;
                }

                document.Profiles[index] = profile with { Name = name.Trim() };
                SaveDocument(document);
                return true;
            }

            return false;
        }
    }

    /// <summary>Serializes a profile rename against activation, rollback, update, and deletion.</summary>
    public async Task<bool> TryRenameProfileAsync(
        string profileId,
        string name,
        CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            async (_, token) =>
            {
                await _profileMutationGate.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    return TryRenameProfileCore(profileId, name);
                }
                finally
                {
                    _profileMutationGate.Release();
                }
            },
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Deletes a user profile after moving any active runtime to the built-in profile.</summary>
    public async Task<bool> TryDeleteProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (admissionLease, token) => TryDeleteProfileCoordinatedAsync(
                profileId,
                admissionLease,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryDeleteProfileCoordinatedAsync(
        string profileId,
        MutationAdmissionLease? admissionLease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        if (StringComparer.Ordinal.Equals(profileId, ProfileCatalogIds.BuiltInDirect))
        {
            return false;
        }

        await _profileMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool isActive;
            string previousActiveProfileId;
            ProfileCatalogDocument updatedDocument;
            lock (_syncLock)
            {
                ProfileCatalogDocument document = LoadDocument();
                if (!TryFindProfile(document, profileId, out _))
                {
                    return false;
                }

                previousActiveProfileId = GetActiveProfileId();
                isActive = StringComparer.Ordinal.Equals(previousActiveProfileId, profileId);
                updatedDocument = CloneDocument(document);
                updatedDocument.Profiles.RemoveAll(profile => StringComparer.Ordinal.Equals(profile.Id, profileId));
                updatedDocument.History.RemoveAll(entry => StringComparer.Ordinal.Equals(entry.ProfileId, profileId));
                if (!updatedDocument.PendingCleanupProfileIds.Contains(profileId, StringComparer.Ordinal))
                {
                    updatedDocument.PendingCleanupProfileIds.Add(profileId);
                }
            }

            if (isActive)
            {
                bool applied = await _runtime
                    .ApplyProfileAsync(ProfileCatalogIds.BuiltInDirect, cancellationToken)
                    .ConfigureAwait(false);
                if (!applied)
                {
                    return false;
                }

                try
                {
                    SetActiveProfilePointer(admissionLease, ProfileCatalogIds.BuiltInDirect);
                }
                catch (Exception settingFailure) when (!ExceptionGraphClassifier.IsProcessFatal(settingFailure))
                {
                    Exception? compensationFailure = await TryRestoreActiveProfileAsync(
                        profileId,
                        previousActiveProfileId,
                        admissionLease).ConfigureAwait(false);
                    if (compensationFailure is not null)
                    {
                        throw new AggregateException(
                            "Active profile pointer commit failed and compensation also failed.",
                            settingFailure,
                            compensationFailure);
                    }

                    throw;
                }
            }

            try
            {
                lock (_syncLock)
                {
                    SaveDocument(updatedDocument);
                }
            }
            catch (Exception catalogFailure) when (isActive && !ExceptionGraphClassifier.IsProcessFatal(catalogFailure))
            {
                Exception? compensationFailure = await TryRestoreActiveProfileAsync(
                    profileId,
                    previousActiveProfileId,
                    admissionLease).ConfigureAwait(false);
                if (compensationFailure is not null)
                {
                    throw new AggregateException(
                        "Profile deletion catalog persistence failed and active-profile compensation also failed.",
                        catalogFailure,
                        compensationFailure);
                }

                throw;
            }

            try
            {
                await RetryPendingProfileCleanupCoreAsync().ConfigureAwait(false);
            }
            catch (Exception maintenanceFailure) when (!ExceptionGraphClassifier.IsProcessFatal(maintenanceFailure))
            {
                TryAppendMaintenanceWarning(
                    "Committed profile deletion cleanup could not run; its tombstone remains pending.",
                    maintenanceFailure);
            }

            return true;
        }
        finally
        {
            _profileMutationGate.Release();
        }
    }

    private void SetActiveProfilePointer(
        MutationAdmissionLease? admissionLease,
        string profileId)
    {
        if (admissionLease is null)
        {
            _settings.ActiveProfileId = profileId;
        }
        else
        {
            IProfileCatalogAdmittedSettings admittedSettings = _admittedSettings
                ?? throw new InvalidOperationException(
                    "The configured profile settings do not support admitted writes.");
            admittedSettings.SetActiveProfileAdmitted(admissionLease, profileId);
        }

        if (!StringComparer.Ordinal.Equals(GetActiveProfileId(), profileId))
        {
            throw new InvalidOperationException("The active profile pointer did not persist the requested identifier.");
        }
    }

    private async Task<Exception?> TryRestoreActiveProfileAsync(
        string runtimeProfileId,
        string previousActiveProfileId,
        MutationAdmissionLease? admissionLease)
    {
        List<Exception> failures = [];
        try
        {
            SetActiveProfilePointer(admissionLease, previousActiveProfileId);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }

        try
        {
            bool restored = await _runtime
                .ApplyProfileAsync(runtimeProfileId, CancellationToken.None)
                .ConfigureAwait(false);
            if (!restored)
            {
                failures.Add(new InvalidOperationException(
                    "The previous active profile runtime could not be restored."));
            }
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            failures.Add(exception);
        }

        return failures.Count switch
        {
            0 => null,
            1 => failures[0],
            _ => new AggregateException(failures),
        };
    }

    /// <summary>Updates the status and timestamp for one subscription link.</summary>
    /// <param name="linkId">Stable link identifier. Must not be null.</param>
    /// <param name="status">New status display text. Must not be null or whitespace.</param>
    /// <returns>True when the link exists and was updated; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="linkId"/> or <paramref name="status"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="status"/> is whitespace.</exception>
    private bool TryUpdateSubscriptionLinkStatus(string linkId, string status)
    {
        ArgumentNullException.ThrowIfNull(linkId);
        ArgumentNullException.ThrowIfNull(status);

        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Subscription link status must not be whitespace.", nameof(status));
        }

        lock (_syncLock)
        {
            ProfileCatalogDocument document = CloneDocument(LoadDocument());
            for (int index = 0; index < document.Links.Count; index++)
            {
                ProfileSubscriptionLink link = document.Links[index];
                if (!StringComparer.Ordinal.Equals(link.Id, linkId))
                {
                    continue;
                }

                document.Links[index] = link with
                {
                    Status = status.Trim(),
                };
                SaveDocument(document);
                return true;
            }

            return false;
        }
    }

    internal Task<bool> TryUpdateSubscriptionLinkStatusAsync(
        string linkId,
        string status,
        CancellationToken cancellationToken)
    {
        return _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(TryUpdateSubscriptionLinkStatus(linkId, status));
            },
            cancellationToken);
    }

    /// <summary>Checks that a subscription link is reachable without importing it.</summary>
    /// <param name="link">Subscription link to check.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Status message written to the link row.</returns>
    /// <exception cref="HttpRequestException">The subscription endpoint cannot be reached successfully.</exception>
    public async Task<string> CheckSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) => CheckSubscriptionLinkCoordinatedAsync(link, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CheckSubscriptionLinkCoordinatedAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim updateGate = GetSubscriptionUpdateGate(link.Id);
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProfileSubscriptionLink currentLink = ResolveCurrentSubscriptionLink(
                link,
                requireEnabled: false);
            try
            {
                EnsureLinkHasHttpUri(currentLink);
                using HttpRequestMessage request = new(HttpMethod.Head, currentLink.Uri);
                using HttpResponseMessage response = await SendWithGetFallbackAsync(request, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                string status = FormatString("ProfileCatalog.Subscription.CheckSucceeded.Format", (int)response.StatusCode);
                TryUpdateSubscriptionLinkStatus(currentLink.Id, status);
                return status;
            }
            catch (Exception exception) when (exception is ArgumentException or HttpRequestException or OperationCanceledException or InvalidOperationException)
            {
                TryUpdateSubscriptionLinkStatus(currentLink.Id, GetString("ProfileCatalog.Subscription.CheckFailed"));
                throw;
            }
        }
        finally
        {
            updateGate.Release();
        }
    }

    /// <summary>Downloads, validates, and imports the selected subscription link into the local profile catalog.</summary>
    /// <param name="link">Subscription link to import.</param>
    /// <param name="cancellationToken">Cancels the download and validation operation.</param>
    /// <returns>Import result containing the imported profile path and estimated counts.</returns>
    /// <exception cref="HttpRequestException">The subscription endpoint cannot be reached successfully.</exception>
    /// <exception cref="ArgumentException">The downloaded configuration is invalid.</exception>
    /// <exception cref="InvalidOperationException">Configuration validation fails.</exception>
    public async Task<ProfileImportResult> ImportSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken)
    {
        ProfileImportResult? result = await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) => ImportSubscriptionLinkCoordinatedAsync(
                link,
                requireDue: false,
                now: DateTimeOffset.MinValue,
                cancellationToken: token),
            cancellationToken).ConfigureAwait(false);
        return result
            ?? throw new InvalidOperationException("The subscription link is no longer eligible for import.");
    }

    /// <summary>Imports a scheduler snapshot only if the same enabled revision is still due.</summary>
    internal Task<ProfileImportResult?> ImportDueSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        return _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) => ImportSubscriptionLinkCoordinatedAsync(
                link,
                requireDue: true,
                now: now,
                cancellationToken: token),
            cancellationToken);
    }

    private async Task<ProfileImportResult?> ImportSubscriptionLinkCoordinatedAsync(
        ProfileSubscriptionLink link,
        bool requireDue,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        SemaphoreSlim updateGate = GetSubscriptionUpdateGate(link.Id);
        await updateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ProfileSubscriptionLink currentLink;
            try
            {
                currentLink = ResolveCurrentSubscriptionLink(link, requireEnabled: true);
            }
            catch (InvalidOperationException) when (requireDue)
            {
                return null;
            }

            if (requireDue && !IsSubscriptionLinkDue(currentLink, now))
            {
                return null;
            }

            return await ImportSubscriptionLinkCoreAsync(currentLink, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            updateGate.Release();
        }
    }

    private async Task<ProfileImportResult> ImportSubscriptionLinkCoreAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken)
    {
        await _profileMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ImportSubscriptionLinkTransactionAsync(link, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileMutationGate.Release();
        }
    }

    private async Task<ProfileImportResult> ImportSubscriptionLinkTransactionAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken)
    {
        try
        {
            EnsureLinkHasHttpUri(link);
            TryUpdateSubscriptionLinkStatus(link.Id, GetString("ProfileCatalog.Subscription.Downloading"));

            string configurationText = await ReadSubscriptionConfigurationAsync(new Uri(link.Uri), cancellationToken).ConfigureAwait(false);
            string profileId = $"subscription-{link.Id}";
            bool isActive = StringComparer.Ordinal.Equals(GetActiveProfileId(), profileId);
            string? previousConfiguration = await _coreConfiguration
                .ReadImportedProfileConfigurationAsync(profileId, cancellationToken)
                .ConfigureAwait(false);
            if (isActive && previousConfiguration is null)
            {
                previousConfiguration = ReadLatestVerifiedHistoryContent(profileId)
                    ?? throw new InvalidOperationException(
                        "Active subscription cannot be updated without a recoverable source baseline.");
            }

            PendingProfileHistory pendingHistory = StageProfileHistory(profileId, configurationText);
            ProfileImportResult importResult;
            try
            {
                if (isActive)
                {
                    ProfileCatalogRuntimeImportResult runtimeResult = await _runtime
                        .ImportAndApplyProfileAsync(
                            profileId,
                            link.Name,
                            configurationText,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (!runtimeResult.IsApplied)
                    {
                        throw new InvalidOperationException(
                            "Active subscription candidate did not become the verified runtime configuration.");
                    }

                    importResult = runtimeResult.Profile;
                }
                else
                {
                    importResult = await _coreConfiguration
                        .ImportProfileConfigurationAsync(profileId, link.Name, configurationText, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch
            {
                DeletePendingHistory(pendingHistory);
                throw;
            }

            try
            {
                lock (_syncLock)
                {
                    ProfileCatalogDocument document = CloneDocument(LoadDocument());
                    UpsertImportedProfile(document, importResult, link.Name);
                    ProfileHistoryCommit historyCommit = FinalizeProfileHistory(
                        document,
                        pendingHistory,
                        importResult,
                        link.Name,
                        isActive ? ProfileHistoryApplyOutcome.Applied : ProfileHistoryApplyOutcome.Stored);
                    UpdateLinkStatus(
                        document,
                        link.Id,
                        GetString("ProfileCatalog.Subscription.Updated"),
                        markSuccessfulUpdate: true);
                    RecordSubscriptionUpdateOutcome(document, link.Id, succeeded: true, DateTimeOffset.Now);
                    try
                    {
                        SaveDocument(document);
                    }
                    catch
                    {
                        RollbackProfileHistoryCommit(historyCommit);
                        throw;
                    }

                    CompleteProfileHistoryCommit(historyCommit);
                }
            }
            catch (Exception catalogFailure) when (!ExceptionGraphClassifier.IsProcessFatal(catalogFailure))
            {
                await CompensateImportedConfigurationAsync(
                    profileId,
                    link.Name,
                    previousConfiguration,
                    isActive,
                    catalogFailure,
                    "Subscription catalog persistence failed and its previous source could not be restored.")
                    .ConfigureAwait(false);

                throw;
            }

            return importResult;
        }
        catch (Exception exception) when (
            exception is ArgumentException
                or HttpRequestException
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException
                or SecurityException
                or OperationCanceledException
                or AggregateException
            && !ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            TryRecordSubscriptionFailure(
                link.Id,
                cancellationToken.IsCancellationRequested
                    ? GetString("ProfileCatalog.Status.Canceled")
                    : GetString("ProfileCatalog.Subscription.UpdateFailed"),
                DateTimeOffset.Now);
            throw;
        }
    }

    private void TryRecordSubscriptionFailure(
        string linkId,
        string status,
        DateTimeOffset attemptedAt)
    {
        try
        {
            lock (_syncLock)
            {
                ProfileCatalogDocument document = CloneDocument(LoadDocument());
                UpdateLinkStatus(document, linkId, status);
                RecordSubscriptionUpdateOutcome(document, linkId, succeeded: false, attemptedAt);
                SaveDocument(document);
            }
        }
        catch (Exception maintenanceFailure) when (!ExceptionGraphClassifier.IsProcessFatal(maintenanceFailure))
        {
            TryAppendMaintenanceWarning(
                "Subscription failure status/backoff could not be persisted.",
                maintenanceFailure);
        }
    }

    /// <summary>Imports a local mihomo-compatible configuration file into the profile catalog.</summary>
    /// <param name="filePath">Absolute local configuration file path. Must not be null or whitespace.</param>
    /// <param name="cancellationToken">Cancels file reading and validation.</param>
    /// <returns>Import result containing the imported profile path and estimated counts.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="filePath"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="filePath"/> is whitespace.</exception>
    /// <exception cref="FileNotFoundException">The file does not exist.</exception>
    /// <exception cref="InvalidOperationException">Configuration validation fails.</exception>
    public async Task<ProfileImportResult> ImportLocalProfileAsync(string filePath, CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) => ImportLocalProfileCoordinatedAsync(filePath, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileImportResult> ImportLocalProfileCoordinatedAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filePath);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Profile file path must not be whitespace.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Profile file was not found.", filePath);
        }

        string configurationText = await ReadBoundedUtf8FileAsync(
            filePath,
            cancellationToken).ConfigureAwait(false);
        string profileName = Path.GetFileNameWithoutExtension(filePath);
        string profileId = $"local-{Guid.NewGuid():N}";
        PendingProfileHistory pendingHistory = StageProfileHistory(profileId, configurationText);
        ProfileImportResult importResult;
        try
        {
            importResult = await _coreConfiguration
                .ImportProfileConfigurationAsync(profileId, profileName, configurationText, cancellationToken)
                .ConfigureAwait(false);
        }
        catch
        {
            DeletePendingHistory(pendingHistory);
            throw;
        }

        try
        {
            lock (_syncLock)
            {
                ProfileCatalogDocument document = CloneDocument(LoadDocument());
                UpsertImportedProfile(document, importResult, Path.GetFileName(filePath));
                ProfileHistoryCommit historyCommit = FinalizeProfileHistory(
                    document,
                    pendingHistory,
                    importResult,
                    Path.GetFileName(filePath),
                    ProfileHistoryApplyOutcome.Stored);
                try
                {
                    SaveDocument(document);
                }
                catch
                {
                    RollbackProfileHistoryCommit(historyCommit);
                    throw;
                }

                CompleteProfileHistoryCommit(historyCommit);
            }
        }
        catch (Exception catalogFailure) when (!ExceptionGraphClassifier.IsProcessFatal(catalogFailure))
        {
            await CompensateImportedConfigurationAsync(
                profileId,
                profileName,
                null,
                false,
                catalogFailure,
                "Local profile catalog persistence failed and its imported source could not be removed.")
                .ConfigureAwait(false);
            throw;
        }

        return importResult;
    }

    /// <summary>Reimports one retained profile version as a new current version.</summary>
    public async Task<ProfileImportResult> RollbackProfileAsync(
        ProfileHistoryEntry historyEntry,
        CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) => RollbackProfileCoordinatedAsync(historyEntry, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileImportResult> RollbackProfileCoordinatedAsync(
        ProfileHistoryEntry historyEntry,
        CancellationToken cancellationToken)
    {
        await _profileMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await RollbackProfileTransactionAsync(historyEntry, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _profileMutationGate.Release();
        }
    }

    private async Task<ProfileImportResult> RollbackProfileTransactionAsync(
        ProfileHistoryEntry historyEntry,
        CancellationToken cancellationToken)
    {
        ConfigurationProfile profile;
        string historyPath;
        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument();
            if (!document.History.Contains(historyEntry)
                || !TryFindProfile(document, historyEntry.ProfileId, out profile))
            {
                throw new ArgumentException("Profile history entry does not exist in the catalog.", nameof(historyEntry));
            }

            historyPath = GetProfileHistoryPath(historyEntry.ProfileId, historyEntry.VersionId);
        }

        string configurationText = await ReadBoundedUtf8FileAsync(
            historyPath,
            cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configurationText)));
        if (!StringComparer.Ordinal.Equals(actualHash, historyEntry.ContentSha256))
        {
            throw new InvalidDataException("Profile history content hash does not match its catalog entry.");
        }
        bool isActive = StringComparer.Ordinal.Equals(GetActiveProfileId(), profile.Id);
        string? previousConfiguration = await _coreConfiguration
            .ReadImportedProfileConfigurationAsync(profile.Id, cancellationToken)
            .ConfigureAwait(false);
        if (previousConfiguration is null)
        {
            previousConfiguration = ReadLatestVerifiedHistoryContent(profile.Id)
                ?? throw new InvalidOperationException(
                    "Profile cannot be rolled back without a recoverable current source baseline.");
        }

        PendingProfileHistory pendingHistory = StageProfileHistory(profile.Id, configurationText);
        ProfileImportResult importResult;
        try
        {
            if (isActive)
            {
                ProfileCatalogRuntimeImportResult runtimeResult = await _runtime
                    .ImportAndApplyProfileAsync(
                        profile.Id,
                        profile.Name,
                        configurationText,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!runtimeResult.IsApplied)
                {
                    throw new InvalidOperationException(
                        "Rollback profile candidate did not become the verified runtime configuration.");
                }

                importResult = runtimeResult.Profile;
            }
            else
            {
                importResult = await _coreConfiguration
                    .ImportProfileConfigurationAsync(profile.Id, profile.Name, configurationText, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            DeletePendingHistory(pendingHistory);
            throw;
        }

        try
        {
            lock (_syncLock)
            {
                ProfileCatalogDocument document = CloneDocument(LoadDocument());
                UpsertImportedProfile(document, importResult with { ProfileName = profile.Name }, profile.SourceName);
                ProfileHistoryCommit historyCommit = FinalizeProfileHistory(
                    document,
                    pendingHistory,
                    importResult,
                    profile.SourceName,
                    ProfileHistoryApplyOutcome.RollbackApplied);
                try
                {
                    SaveDocument(document);
                }
                catch
                {
                    RollbackProfileHistoryCommit(historyCommit);
                    throw;
                }

                CompleteProfileHistoryCommit(historyCommit);
            }
        }
        catch (Exception catalogFailure) when (!ExceptionGraphClassifier.IsProcessFatal(catalogFailure))
        {
            await CompensateImportedConfigurationAsync(
                profile.Id,
                profile.Name,
                previousConfiguration,
                isActive,
                catalogFailure,
                "Profile rollback catalog persistence failed and its previous source could not be restored.")
                .ConfigureAwait(false);

            throw;
        }

        return importResult with { ProfileName = profile.Name };
    }

    /// <summary>Validates a catalog profile and updates its visible status.</summary>
    /// <param name="profile">Profile row to validate.</param>
    /// <param name="cancellationToken">Cancels external mihomo validation.</param>
    /// <returns>Import-style validation result containing current profile metrics.</returns>
    /// <exception cref="ArgumentException">The selected profile cannot be validated.</exception>
    /// <exception cref="FileNotFoundException">The profile configuration file is missing.</exception>
    /// <exception cref="InvalidOperationException">Configuration validation fails.</exception>
    public async Task<ProfileImportResult> ValidateProfileAsync(ConfigurationProfile profile, CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) => ValidateProfileCoordinatedAsync(profile, token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ProfileImportResult> ValidateProfileCoordinatedAsync(
        ConfigurationProfile profile,
        CancellationToken cancellationToken)
    {
        if (StringComparer.Ordinal.Equals(profile.Id, ProfileCatalogIds.BuiltInDirect))
        {
            CoreConfigurationState state = _coreConfiguration.EnsureDefaultConfiguration();
            ProfileImportResult result = new(profile.Id, profile.Name, state.ConfigPath, 0, 1, GetString("ProfileCatalog.Profile.BuiltInDirectAvailable"));
            TryUpdateProfileStatus(profile.Id, GetString("ProfileCatalog.Status.Available"), result.NodeCount, result.RuleCount);
            return result;
        }

        try
        {
            ProfileImportResult result = await _coreConfiguration
                .ValidateImportedProfileAsync(profile.Id, cancellationToken)
                .ConfigureAwait(false);

            TryUpdateProfileStatus(profile.Id, GetString("ProfileCatalog.Profile.ValidationSucceeded"), result.NodeCount, result.RuleCount);
            return result with { ProfileName = profile.Name };
        }
        catch
        {
            TryUpdateProfileStatus(
                profile.Id,
                cancellationToken.IsCancellationRequested
                    ? GetString("ProfileCatalog.Status.Canceled")
                    : GetString("ProfileCatalog.Profile.ValidationFailed"),
                profile.NodeCount,
                profile.RuleCount);
            throw;
        }
    }

    /// <summary>Applies and readiness-verifies a profile before committing the legacy active pointer.</summary>
    public async Task<bool> TryApplyActiveProfileAsync(
        string profileId,
        CancellationToken cancellationToken)
    {
        return await _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            (admissionLease, token) => TryApplyActiveProfileCoordinatedAsync(
                profileId,
                admissionLease,
                token),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> TryApplyActiveProfileCoordinatedAsync(
        string profileId,
        MutationAdmissionLease? admissionLease,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        await _profileMutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string previousActiveProfileId;
            lock (_syncLock)
            {
                if (!TryFindProfile(LoadDocument(), profileId, out _))
                {
                    return false;
                }

                previousActiveProfileId = GetActiveProfileId();
            }

            bool applied = await _runtime.ApplyProfileAsync(profileId, cancellationToken).ConfigureAwait(false);
            if (!applied)
            {
                return false;
            }

            try
            {
                lock (_syncLock)
                {
                    SetActiveProfilePointer(admissionLease, profileId);
                }
                return true;
            }
            catch (Exception settingFailure) when (!ExceptionGraphClassifier.IsProcessFatal(settingFailure))
            {
                Exception? compensationFailure = await TryRestoreActiveProfileAsync(
                    previousActiveProfileId,
                    previousActiveProfileId,
                    admissionLease).ConfigureAwait(false);
                if (compensationFailure is not null)
                {
                    throw new AggregateException(
                        "Active profile pointer commit failed and runtime compensation also failed.",
                        settingFailure,
                        compensationFailure);
                }

                throw;
            }
        }
        finally
        {
            _profileMutationGate.Release();
        }
    }

    /// <summary>Forgets the cached catalog after local profile data has been deleted externally.</summary>
    internal void ResetAfterDataDeletion()
    {
        lock (_syncLock)
        {
            _cachedDocument = null;
        }
    }

    /// <summary>Retries durable post-delete source/history cleanup without reopening a committed delete.</summary>
    internal Task RetryPendingProfileCleanupAsync(CancellationToken cancellationToken)
    {
        return _mutationCoordinator.ExecuteAsync(
            Guid.NewGuid(),
            async (_, token) =>
            {
                token.ThrowIfCancellationRequested();
                await RetryPendingProfileCleanupCoreAsync().ConfigureAwait(false);
                return true;
            },
            cancellationToken);
    }

    private async Task RetryPendingProfileCleanupCoreAsync()
    {
        IReadOnlyList<string> pendingProfileIds;
        lock (_syncLock)
        {
            pendingProfileIds = [.. LoadDocument().PendingCleanupProfileIds];
        }

        foreach (string profileId in pendingProfileIds)
        {
            bool historyRemoved = TryDeleteProfileHistoryDirectory(profileId);
            bool sourceRemoved = true;
            try
            {
                _ = await _runtime
                    .DeleteImportedProfileAsync(profileId, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                sourceRemoved = false;
                TryAppendMaintenanceWarning(
                    "A deleted profile source directory could not be removed; cleanup remains pending.",
                    exception);
            }

            if (!historyRemoved || !sourceRemoved)
            {
                continue;
            }

            try
            {
                lock (_syncLock)
                {
                    ProfileCatalogDocument document = CloneDocument(LoadDocument());
                    if (document.PendingCleanupProfileIds.RemoveAll(id =>
                            StringComparer.Ordinal.Equals(id, profileId)) > 0)
                    {
                        SaveDocument(document);
                    }
                }
            }
            catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                TryAppendMaintenanceWarning(
                    "Completed profile cleanup could not be acknowledged; it will be retried.",
                    exception);
            }
        }
    }

    /// <summary>Creates the shared HTTP client used for subscription operations.</summary>
    /// <returns>Configured HTTP client instance.</returns>
    private static HttpClient CreateHttpClient()
    {
        return new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    /// <summary>Sends a HEAD request and falls back to GET when the server does not support HEAD.</summary>
    /// <param name="request">HEAD request to send. Must not be null.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>HTTP response owned by the caller.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="request"/> is null.</exception>
    private static async Task<HttpResponseMessage> SendWithGetFallbackAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        HttpResponseMessage response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is not (HttpStatusCode.MethodNotAllowed or HttpStatusCode.NotImplemented))
        {
            return response;
        }

        response.Dispose();
        using HttpRequestMessage fallbackRequest = new(HttpMethod.Get, request.RequestUri);
        return await HttpClient.SendAsync(fallbackRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a subscription profile response with a hard byte limit.</summary>
    private static async Task<string> ReadSubscriptionConfigurationAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);

        using HttpRequestMessage request = new(HttpMethod.Get, uri);
        using HttpResponseMessage response = await HttpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is long contentLength
            && contentLength > MaxSubscriptionDownloadBytes)
        {
            throw new InvalidOperationException("Subscription profile is larger than the supported limit.");
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + bytesRead > MaxSubscriptionDownloadBytes)
            {
                throw new InvalidOperationException("Subscription profile is larger than the supported limit.");
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return DecodeStrictUtf8(buffer.ToArray());
    }

    /// <summary>Reads a local profile with byte-growth protection and strict UTF-8 decoding.</summary>
    private static async Task<string> ReadBoundedUtf8FileAsync(
        string filePath,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 8192,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > MaxProfileConfigurationBytes)
        {
            throw new InvalidDataException("Profile configuration is larger than the supported limit.");
        }

        using MemoryStream buffer = new();
        byte[] chunk = new byte[8192];
        int bytesRead;
        while ((bytesRead = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + bytesRead > MaxProfileConfigurationBytes)
            {
                throw new InvalidDataException("Profile configuration is larger than the supported limit.");
            }

            buffer.Write(chunk, 0, bytesRead);
        }

        return DecodeStrictUtf8(buffer.ToArray());
    }

    private static string DecodeStrictUtf8(byte[] bytes)
    {
        try
        {
            string text = StrictUtf8.GetString(bytes);
            return text.Length > 0 && text[0] == '\uFEFF'
                ? text[1..]
                : text;
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("Profile configuration must be valid UTF-8.", exception);
        }
    }

    /// <summary>Validates that <paramref name="link"/> contains an absolute HTTP or HTTPS URI.</summary>
    /// <param name="link">Subscription link to validate.</param>
    /// <exception cref="ArgumentException">The link URI is invalid.</exception>
    private static void EnsureLinkHasHttpUri(ProfileSubscriptionLink link)
    {
        if (!Uri.TryCreate(link.Uri, UriKind.Absolute, out Uri? uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Subscription link URI must be an absolute HTTP or HTTPS URI.", nameof(link));
        }
    }

    /// <summary>Parses and validates a subscription URI.</summary>
    private static Uri ParseSubscriptionUri(string uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Subscription link URI must be an absolute HTTP or HTTPS URI.", nameof(uri));
        }

        return parsedUri;
    }

    /// <summary>Reads and hash-verifies the newest recoverable version for one profile.</summary>
    private string? ReadLatestVerifiedHistoryContent(string profileId)
    {
        ProfileHistoryEntry? latest;
        lock (_syncLock)
        {
            latest = LoadDocument().History
                .Where(entry => StringComparer.Ordinal.Equals(entry.ProfileId, profileId))
                .OrderByDescending(entry => entry.CreatedAt)
                .Cast<ProfileHistoryEntry?>()
                .FirstOrDefault();
        }

        if (latest is not { } entry)
        {
            return null;
        }

        string content = File.ReadAllText(GetProfileHistoryPath(entry.ProfileId, entry.VersionId));
        string actualHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
        if (!StringComparer.Ordinal.Equals(actualHash, entry.ContentSha256))
        {
            throw new InvalidDataException("Profile history content hash does not match its catalog entry.");
        }

        return content;
    }

    /// <summary>Restores or removes a profile source after durable catalog persistence fails.</summary>
    private async Task CompensateImportedConfigurationAsync(
        string profileId,
        string profileName,
        string? previousConfiguration,
        bool applyRuntime,
        Exception catalogFailure,
        string failureMessage)
    {
        try
        {
            if (previousConfiguration is null)
            {
                await _runtime
                    .DeleteImportedProfileAsync(profileId, CancellationToken.None)
                    .ConfigureAwait(false);
                return;
            }

            if (applyRuntime)
            {
                ProfileCatalogRuntimeImportResult restoreResult = await _runtime
                    .ImportAndApplyProfileAsync(
                        profileId,
                        profileName,
                        previousConfiguration,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (!restoreResult.IsApplied)
                {
                    throw new InvalidOperationException(
                        "The previous profile source did not become the verified runtime configuration.");
                }

                return;
            }

            await _coreConfiguration
                .ImportProfileConfigurationAsync(
                    profileId,
                    profileName,
                    previousConfiguration,
                    CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (Exception compensationFailure) when (!ExceptionGraphClassifier.IsProcessFatal(compensationFailure))
        {
            throw new AggregateException(failureMessage, catalogFailure, compensationFailure);
        }
    }

    /// <summary>Gets the stable single-flight gate for one subscription link.</summary>
    private SemaphoreSlim GetSubscriptionUpdateGate(string linkId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(linkId);
        lock (_syncLock)
        {
            if (!_subscriptionUpdateGates.TryGetValue(linkId, out SemaphoreSlim? gate))
            {
                gate = new SemaphoreSlim(1, 1);
                _subscriptionUpdateGates.Add(linkId, gate);
            }

            return gate;
        }
    }

    /// <summary>Re-resolves a caller snapshot after the per-link gate and rejects stale definitions.</summary>
    private ProfileSubscriptionLink ResolveCurrentSubscriptionLink(
        ProfileSubscriptionLink expectedLink,
        bool requireEnabled)
    {
        lock (_syncLock)
        {
            ProfileSubscriptionLink? current = LoadDocument().Links
                .Cast<ProfileSubscriptionLink?>()
                .FirstOrDefault(candidate => candidate is { } value
                    && StringComparer.Ordinal.Equals(value.Id, expectedLink.Id));
            if (current is not { } currentLink)
            {
                throw new InvalidOperationException("The subscription link was deleted before the operation started.");
            }

            if (currentLink.Revision != expectedLink.Revision
                || !StringComparer.Ordinal.Equals(currentLink.Uri, expectedLink.Uri))
            {
                throw new InvalidOperationException("The subscription link definition changed before the operation started.");
            }

            if (requireEnabled && !currentLink.IsEnabled)
            {
                throw new InvalidOperationException("The subscription link was disabled before the import started.");
            }

            return currentLink;
        }
    }

    /// <summary>Rechecks persisted enabled/due state while the per-link gate is still held.</summary>
    private bool IsSubscriptionLinkDue(ProfileSubscriptionLink expectedLink, DateTimeOffset now)
    {
        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument();
            ProfileSubscriptionLink? current = document.Links
                .Cast<ProfileSubscriptionLink?>()
                .FirstOrDefault(candidate => candidate is { } value
                    && StringComparer.Ordinal.Equals(value.Id, expectedLink.Id));
            if (current is not { } currentLink
                || !currentLink.IsEnabled
                || currentLink.Revision != expectedLink.Revision
                || !StringComparer.Ordinal.Equals(currentLink.Uri, expectedLink.Uri))
            {
                return false;
            }

            DateTimeOffset nextAttemptAt = FindScheduleState(document, currentLink.Id)?.NextAttemptAt
                ?? GetInitialNextAttemptAt(currentLink, now);
            return nextAttemptAt <= now;
        }
    }

    private static ProfileSubscriptionScheduleState? FindScheduleState(
        ProfileCatalogDocument document,
        string linkId)
    {
        return document.SubscriptionSchedules.Find(state =>
            StringComparer.Ordinal.Equals(state.LinkId, linkId));
    }

    private static DateTimeOffset GetInitialNextAttemptAt(
        ProfileSubscriptionLink link,
        DateTimeOffset now)
    {
        return link.LastUpdatedAt > DateTimeOffset.UnixEpoch
            ? link.LastUpdatedAt.AddHours(Math.Max(1, link.UpdateIntervalHours))
            : now;
    }

    /// <summary>Updates one persisted scheduler state after an attempted subscription import.</summary>
    private static void RecordSubscriptionUpdateOutcome(
        ProfileCatalogDocument document,
        string linkId,
        bool succeeded,
        DateTimeOffset attemptedAt)
    {
        ProfileSubscriptionLink? link = document.Links
            .Cast<ProfileSubscriptionLink?>()
            .FirstOrDefault(candidate => candidate is { } value && StringComparer.Ordinal.Equals(value.Id, linkId));
        if (link is not { } existingLink)
        {
            return;
        }

        ProfileSubscriptionScheduleState state = GetOrCreateScheduleState(document, existingLink);
        state.LastAttemptAt = attemptedAt;
        if (succeeded)
        {
            state.ConsecutiveFailures = 0;
            state.LastSuccessfulUpdateAt = attemptedAt;
            state.NextAttemptAt = attemptedAt.AddHours(existingLink.UpdateIntervalHours);
            return;
        }

        state.ConsecutiveFailures = Math.Min(state.ConsecutiveFailures + 1, 31);
        int exponent = Math.Min(state.ConsecutiveFailures - 1, 10);
        double retryMinutes = MinimumSubscriptionRetryDelay.TotalMinutes * Math.Pow(2, exponent);
        TimeSpan retryDelay = TimeSpan.FromMinutes(Math.Min(
            retryMinutes,
            MaximumSubscriptionRetryDelay.TotalMinutes));
        state.NextAttemptAt = attemptedAt.Add(retryDelay);
    }

    /// <summary>Finds or migrates persisted scheduler state for one link.</summary>
    private static ProfileSubscriptionScheduleState GetOrCreateScheduleState(
        ProfileCatalogDocument document,
        ProfileSubscriptionLink link)
    {
        foreach (ProfileSubscriptionScheduleState state in document.SubscriptionSchedules)
        {
            if (StringComparer.Ordinal.Equals(state.LinkId, link.Id))
            {
                return state;
            }
        }

        DateTimeOffset? lastSuccessful = link.LastUpdatedAt > DateTimeOffset.UnixEpoch
            ? link.LastUpdatedAt
            : null;
        ProfileSubscriptionScheduleState migrated = new()
        {
            LinkId = link.Id,
            NextAttemptAt = lastSuccessful is { } value
                ? value.AddHours(Math.Max(1, link.UpdateIntervalHours))
                : DateTimeOffset.Now,
            ConsecutiveFailures = 0,
            LastSuccessfulUpdateAt = lastSuccessful,
            LastAttemptAt = null,
        };
        document.SubscriptionSchedules.Add(migrated);
        return migrated;
    }

    /// <summary>Stages configuration text before the core import can mutate the current profile.</summary>
    private PendingProfileHistory StageProfileHistory(string profileId, string configurationText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        ArgumentNullException.ThrowIfNull(configurationText);

        string pendingDirectory = Path.Combine(_historyRoot, ".pending");
        Directory.CreateDirectory(pendingDirectory);
        string versionId = Guid.NewGuid().ToString("N");
        string pendingPath = Path.Combine(pendingDirectory, versionId + ".yaml");
        File.WriteAllText(pendingPath, configurationText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        string contentSha256 = Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(configurationText)));
        return new PendingProfileHistory(profileId, versionId, DateTimeOffset.Now, pendingPath, contentSha256);
    }

    /// <summary>Promotes staged text and records a successful immutable profile version.</summary>
    private ProfileHistoryCommit FinalizeProfileHistory(
        ProfileCatalogDocument document,
        PendingProfileHistory pendingHistory,
        ProfileImportResult importResult,
        string sourceName,
        ProfileHistoryApplyOutcome applyOutcome)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sourceName);
        if (!StringComparer.Ordinal.Equals(pendingHistory.ProfileId, importResult.ProfileId))
        {
            DeletePendingHistory(pendingHistory);
            throw new InvalidOperationException("Imported profile identifier does not match the staged profile history.");
        }

        string historyPath = GetProfileHistoryPath(pendingHistory.ProfileId, pendingHistory.VersionId);
        Directory.CreateDirectory(Path.GetDirectoryName(historyPath)!);
        File.Move(pendingHistory.PendingPath, historyPath);
        document.History.Add(new ProfileHistoryEntry(
            pendingHistory.VersionId,
            pendingHistory.ProfileId,
            pendingHistory.CreatedAt,
            sourceName,
            importResult.NodeCount,
            importResult.RuleCount,
            pendingHistory.ContentSha256,
            applyOutcome));
        IReadOnlyList<string> obsoletePaths = PruneProfileHistory(document, pendingHistory.ProfileId);
        return new ProfileHistoryCommit(historyPath, obsoletePaths);
    }

    /// <summary>Retains only the newest bounded set of versions for one profile.</summary>
    private IReadOnlyList<string> PruneProfileHistory(ProfileCatalogDocument document, string profileId)
    {
        List<ProfileHistoryEntry> obsoleteEntries = [.. document.History
            .Where(entry => StringComparer.Ordinal.Equals(entry.ProfileId, profileId))
            .OrderByDescending(entry => entry.CreatedAt)
            .Skip(MaxProfileHistoryEntriesPerProfile)];
        List<string> obsoletePaths = [];
        foreach (ProfileHistoryEntry entry in obsoleteEntries)
        {
            document.History.Remove(entry);
            obsoletePaths.Add(GetProfileHistoryPath(entry.ProfileId, entry.VersionId));
        }

        return obsoletePaths;
    }

    private void CompleteProfileHistoryCommit(ProfileHistoryCommit commit)
    {
        try
        {
            foreach (string obsoletePath in commit.ObsoletePaths)
            {
                try
                {
                    File.Delete(obsoletePath);
                }
                catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
                {
                    TryAppendMaintenanceWarning(
                        "An obsolete profile history file could not be removed.",
                        exception);
                }
            }
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            TryAppendMaintenanceWarning(
                "Profile history post-commit maintenance did not complete.",
                exception);
        }
    }

    private void TryAppendMaintenanceWarning(string message, Exception exception)
    {
        try
        {
            _log.AppendLog(
                "Warning",
                "Profiles",
                message,
                exception.GetType().Name);
        }
        catch (Exception loggingFailure) when (!ExceptionGraphClassifier.IsProcessFatal(loggingFailure))
        {
            // Post-commit maintenance and its diagnostics must never reopen a committed transaction.
        }
    }

    private void RollbackProfileHistoryCommit(ProfileHistoryCommit commit)
    {
        try
        {
            File.Delete(commit.NewHistoryPath);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            TryAppendMaintenanceWarning(
                "A profile history rollback file could not be removed.",
                exception);
        }
    }

    /// <summary>Deletes a staged history file after import failure.</summary>
    private static void DeletePendingHistory(PendingProfileHistory pendingHistory)
    {
        File.Delete(pendingHistory.PendingPath);
    }

    /// <summary>Builds a path from generated identifiers without placing user text in a path segment.</summary>
    private string GetProfileHistoryPath(string profileId, string versionId)
    {
        if (!Guid.TryParseExact(versionId, "N", out _))
        {
            throw new InvalidDataException("Profile history version identifier is invalid.");
        }

        string profileDirectoryName = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(profileId))).ToLowerInvariant();
        return Path.Combine(_historyRoot, profileDirectoryName, versionId + ".yaml");
    }

    /// <summary>Removes retained configuration versions for a deleted profile.</summary>
    private bool TryDeleteProfileHistoryDirectory(string profileId)
    {
        string samplePath = GetProfileHistoryPath(profileId, Guid.Empty.ToString("N"));
        string? profileDirectory = Path.GetDirectoryName(samplePath);
        try
        {
            if (!string.IsNullOrWhiteSpace(profileDirectory) && Directory.Exists(profileDirectory))
            {
                Directory.Delete(profileDirectory, recursive: true);
            }

            return true;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            TryAppendMaintenanceWarning(
                "Deleted profile history could not be removed.",
                exception);
            return false;
        }
    }

    /// <summary>Finds a profile by stable identifier in an already-loaded document.</summary>
    private static bool TryFindProfile(
        ProfileCatalogDocument document,
        string profileId,
        out ConfigurationProfile profile)
    {
        foreach (ConfigurationProfile candidate in document.Profiles)
        {
            if (StringComparer.Ordinal.Equals(candidate.Id, profileId))
            {
                profile = candidate;
                return true;
            }
        }

        profile = default;
        return false;
    }

    /// <summary>Upserts an imported profile row into the catalog document.</summary>
    /// <param name="document">Catalog document to mutate. Must not be null.</param>
    /// <param name="importResult">Import result used for profile metadata.</param>
    /// <param name="sourceName">Profile source display name. Must not be null.</param>
    private void UpsertImportedProfile(ProfileCatalogDocument document, ProfileImportResult importResult, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sourceName);

        ConfigurationProfile profile = new(
            importResult.ProfileId,
            importResult.ProfileName,
            sourceName,
            GetString("ProfileCatalog.Status.Available"),
            DateTimeOffset.Now,
            importResult.NodeCount,
            importResult.RuleCount,
            false);

        document.PendingCleanupProfileIds.RemoveAll(id =>
            StringComparer.Ordinal.Equals(id, profile.Id));

        for (int index = 0; index < document.Profiles.Count; index++)
        {
            if (StringComparer.Ordinal.Equals(document.Profiles[index].Id, profile.Id))
            {
                document.Profiles[index] = profile;
                return;
            }
        }

        document.Profiles.Add(profile);
    }

    /// <summary>Updates one link status in an already-loaded catalog document.</summary>
    /// <param name="document">Catalog document to mutate. Must not be null.</param>
    /// <param name="linkId">Stable link identifier. Must not be null.</param>
    /// <param name="status">New link status. Must not be null.</param>
    private static void UpdateLinkStatus(
        ProfileCatalogDocument document,
        string linkId,
        string status,
        bool markSuccessfulUpdate = false)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(linkId);
        ArgumentNullException.ThrowIfNull(status);

        for (int index = 0; index < document.Links.Count; index++)
        {
            ProfileSubscriptionLink link = document.Links[index];
            if (!StringComparer.Ordinal.Equals(link.Id, linkId))
            {
                continue;
            }

            document.Links[index] = link with
            {
                LastUpdatedAt = markSuccessfulUpdate ? DateTimeOffset.Now : link.LastUpdatedAt,
                Status = status,
            };
            return;
        }
    }

    /// <summary>Updates one profile status and metrics when the profile exists.</summary>
    /// <param name="profileId">Stable profile identifier. Must not be null.</param>
    /// <param name="status">New profile status. Must not be null.</param>
    /// <param name="nodeCount">Current node count.</param>
    /// <param name="ruleCount">Current rule count.</param>
    /// <returns>True when the profile exists and was updated; otherwise false.</returns>
    private bool TryUpdateProfileStatus(string profileId, string status, int nodeCount, int ruleCount)
    {
        ArgumentNullException.ThrowIfNull(profileId);
        ArgumentNullException.ThrowIfNull(status);

        lock (_syncLock)
        {
            ProfileCatalogDocument document = CloneDocument(LoadDocument());
            for (int index = 0; index < document.Profiles.Count; index++)
            {
                ConfigurationProfile profile = document.Profiles[index];
                if (!StringComparer.Ordinal.Equals(profile.Id, profileId))
                {
                    continue;
                }

                document.Profiles[index] = profile with
                {
                    Status = status,
                    UpdatedAt = DateTimeOffset.Now,
                    NodeCount = nodeCount,
                    RuleCount = ruleCount,
                };
                SaveDocument(document);
                return true;
            }

            return false;
        }
    }

    /// <summary>Reads the active profile identifier, normalizing missing values to the built-in profile.</summary>
    /// <returns>Active profile identifier; never null.</returns>
    private string GetActiveProfileId()
    {
        string activeProfileId = _settings.ActiveProfileId;
        return string.IsNullOrWhiteSpace(activeProfileId) ? ProfileCatalogIds.BuiltInDirect : activeProfileId;
    }

    /// <summary>Loads the profile catalog document from disk, creating a default document when needed.</summary>
    /// <returns>Loaded profile catalog document; never null.</returns>
    private ProfileCatalogDocument LoadDocument()
    {
        return LoadDocument(GetString);
    }

    /// <summary>Loads the catalog with an explicit fallback text source.</summary>
    private ProfileCatalogDocument LoadDocument(Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);

        if (_cachedDocument is not null)
        {
            return _cachedDocument;
        }

        if (File.Exists(_catalogPath))
        {
            try
            {
                string json = File.ReadAllText(_catalogPath);
                ProfileCatalogDocument? document = JsonSerializer.Deserialize<ProfileCatalogDocument>(json);
                if (document is not null)
                {
                    _cachedDocument = EnsureBuiltInProfile(document, getString);
                    return _cachedDocument;
                }
            }
            catch (JsonException exception)
            {
                _log.AppendLog("Warning", "Profiles", "Profile catalog JSON could not be read.", exception.Message);
            }
            catch (IOException exception)
            {
                _log.AppendLog("Warning", "Profiles", "Profile catalog file could not be read.", exception.Message);
            }
        }

        _cachedDocument = BuildDefaultDocument(getString);
        return _cachedDocument;
    }

    /// <summary>Saves the profile catalog document to disk and updates the in-memory cache.</summary>
    /// <param name="document">Profile catalog document to save. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    private void SaveDocument(ProfileCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        EnsureCatalogDirectoryExists();
        string json = JsonSerializer.Serialize(document, JsonOptions);
        DurableAtomicFile.WriteText(_catalogPath, json);
        _cachedDocument = document;
    }

    private static ProfileCatalogDocument CloneDocument(ProfileCatalogDocument document)
    {
        string json = JsonSerializer.Serialize(document, JsonOptions);
        return JsonSerializer.Deserialize<ProfileCatalogDocument>(json)
            ?? throw new InvalidDataException("Profile catalog clone could not be created.");
    }

    private void EnsureCatalogDirectoryExists()
    {
        string? dataDirectory = Path.GetDirectoryName(_catalogPath);
        if (!string.IsNullOrWhiteSpace(dataDirectory))
        {
            Directory.CreateDirectory(dataDirectory);
        }
    }

    /// <summary>Ensures the catalog document contains the built-in direct profile.</summary>
    /// <param name="document">Catalog document to inspect. Must not be null.</param>
    /// <returns>The original document with the built-in profile inserted when necessary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    private ProfileCatalogDocument EnsureBuiltInProfile(
        ProfileCatalogDocument document,
        Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(getString);

        document.Profiles ??= [];
        document.Links ??= [];
        document.History ??= [];
        document.SubscriptionSchedules ??= [];
        document.PendingCleanupProfileIds ??= [];
        document.PendingCleanupProfileIds = [.. document.PendingCleanupProfileIds
            .Where(id => !string.IsNullOrWhiteSpace(id)
                && !StringComparer.Ordinal.Equals(id, ProfileCatalogIds.BuiltInDirect))
            .Distinct(StringComparer.Ordinal)];
        document.SubscriptionSchedules.RemoveAll(state =>
            state is null
            || string.IsNullOrWhiteSpace(state.LinkId)
            || !document.Links.Exists(link => StringComparer.Ordinal.Equals(link.Id, state.LinkId)));
        foreach (ProfileSubscriptionLink link in document.Links)
        {
            _ = GetOrCreateScheduleState(document, link);
        }
        RemoveObsoletePreviewProfiles(document.Profiles);

        foreach (ConfigurationProfile profile in document.Profiles)
        {
            if (StringComparer.Ordinal.Equals(profile.Id, ProfileCatalogIds.BuiltInDirect))
            {
                return document;
            }
        }

        document.Profiles.Insert(0, BuildDefaultProfile(getString));
        return document;
    }

    /// <summary>Builds the default catalog document used on first run.</summary>
    /// <returns>A catalog document containing the built-in direct profile and no user links.</returns>
    private ProfileCatalogDocument BuildDefaultDocument(Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);

        return new ProfileCatalogDocument
        {
            Profiles =
            [
                BuildDefaultProfile(getString),
            ],
            Links = [],
            History = [],
            SubscriptionSchedules = [],
            PendingCleanupProfileIds = [],
        };
    }

    /// <summary>Removes obsolete preview profiles from catalogs created by earlier development builds.</summary>
    /// <param name="profiles">Mutable catalog profile list. Must not be null.</param>
    private static void RemoveObsoletePreviewProfiles(List<ConfigurationProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        profiles.RemoveAll(profile => StringComparer.Ordinal.Equals(profile.Id, ObsoleteSampleProfileId));
    }

    /// <summary>Builds the built-in direct profile.</summary>
    /// <returns>The built-in direct profile row.</returns>
    private ConfigurationProfile BuildDefaultProfile(Func<string, string> getString)
    {
        ArgumentNullException.ThrowIfNull(getString);

        return new ConfigurationProfile(
            ProfileCatalogIds.BuiltInDirect,
            getString("ProfileCatalog.BuiltInDirect.Name"),
            "Clash#",
            getString("ProfileCatalog.Status.Available"),
            DateTimeOffset.Now,
            0,
            1,
            false);
    }

    private string GetString(string key)
    {
        return _getString(key);
    }

    private string FormatString(string key, params object[] args)
    {
        return string.Format(CultureInfo.CurrentCulture, GetString(key), args);
    }

    /// <summary>Serializable profile catalog document stored on disk.</summary>
    private sealed class ProfileCatalogDocument
    {
        /// <summary>Gets or sets configuration profile rows.</summary>
        /// <value>Mutable list used by the catalog service; never null after construction.</value>
        public List<ConfigurationProfile> Profiles { get; set; } = [];

        /// <summary>Gets or sets subscription link rows.</summary>
        /// <value>Mutable list used by the catalog service; never null after construction.</value>
        public List<ProfileSubscriptionLink> Links { get; set; } = [];

        /// <summary>Gets or sets immutable successful profile-version metadata.</summary>
        public List<ProfileHistoryEntry> History { get; set; } = [];

        /// <summary>Gets or sets durable automatic-update schedule state.</summary>
        public List<ProfileSubscriptionScheduleState> SubscriptionSchedules { get; set; } = [];

        /// <summary>Gets or sets durable post-catalog cleanup tombstones.</summary>
        public List<string> PendingCleanupProfileIds { get; set; } = [];
    }

    /// <summary>Durable per-link scheduler state kept separate from legacy visible timestamps.</summary>
    private sealed class ProfileSubscriptionScheduleState
    {
        public string LinkId { get; set; } = string.Empty;

        public DateTimeOffset NextAttemptAt { get; set; }

        public int ConsecutiveFailures { get; set; }

        public DateTimeOffset? LastSuccessfulUpdateAt { get; set; }

        public DateTimeOffset? LastAttemptAt { get; set; }
    }

    /// <summary>Configuration text staged until a core import succeeds.</summary>
    private readonly record struct PendingProfileHistory(
        string ProfileId,
        string VersionId,
        DateTimeOffset CreatedAt,
        string PendingPath,
        string ContentSha256);

    private readonly record struct ProfileHistoryCommit(
        string NewHistoryPath,
        IReadOnlyList<string> ObsoletePaths);
}
