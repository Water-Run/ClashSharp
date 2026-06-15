/*
 * Profile Catalog Service
 * Provides local configuration profile and subscription-link data for WinUI pages
 *
 * @author: WaterRun
 * @file: Service/ProfileCatalogService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides local configuration profile and subscription-link data for WinUI pages.</summary>
/// <remarks>
/// Invariants: At least one built-in profile is always available.
/// Thread safety: Public members serialize mutable state through a private lock.
/// Side effects: Reads and writes the local profile catalog JSON file; persists active profile selection to application settings.
/// </remarks>
public sealed class ProfileCatalogService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="ProfileCatalogService"/> instance.</value>
    public static ProfileCatalogService Instance { get; } = new();

    /// <summary>Synchronization object guarding active profile mutations for this service lifetime.</summary>
    private readonly object _syncLock = new();

    /// <summary>Absolute path to the profile catalog JSON file.</summary>
    private readonly string _catalogPath;

    /// <summary>Cached catalog document loaded from disk during this service lifetime.</summary>
    private ProfileCatalogDocument? _cachedDocument;

    /// <summary>Obsolete preview profile identifier removed from early catalog builds.</summary>
    private const string ObsoleteSampleProfileId = "sample-rule-profile";

    /// <summary>Shared HTTP client used for subscription checks and downloads.</summary>
    private static readonly HttpClient HttpClient = CreateHttpClient();

    /// <summary>Initializes the profile catalog service.</summary>
    private ProfileCatalogService()
    {
        string dataDirectory = AppDataPathService.ResolveLocalDataDirectory();
        Directory.CreateDirectory(dataDirectory);
        _catalogPath = Path.Combine(dataDirectory, "ProfileCatalog.json");
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

    /// <summary>Adds a subscription link to the local profile catalog.</summary>
    /// <param name="name">User-facing link name. Must not be null or whitespace.</param>
    /// <param name="uri">Subscription URI. Must not be null, whitespace, or invalid.</param>
    /// <returns>The added subscription link.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="uri"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="name"/> is whitespace or <paramref name="uri"/> is invalid.</exception>
    public ProfileSubscriptionLink AddSubscriptionLink(string name, string uri)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(uri);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Subscription link name must not be whitespace.", nameof(name));
        }

        if (!Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsedUri)
            || (parsedUri.Scheme != Uri.UriSchemeHttp && parsedUri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("Subscription link URI must be an absolute HTTP or HTTPS URI.", nameof(uri));
        }

        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument();
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
                DateTimeOffset.Now,
                "已添加");

            document.Links.Add(link);
            SaveDocument(document);
            return link;
        }
    }

    /// <summary>Updates the status and timestamp for one subscription link.</summary>
    /// <param name="linkId">Stable link identifier. Must not be null.</param>
    /// <param name="status">New status display text. Must not be null or whitespace.</param>
    /// <returns>True when the link exists and was updated; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="linkId"/> or <paramref name="status"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="status"/> is whitespace.</exception>
    public bool TryUpdateSubscriptionLinkStatus(string linkId, string status)
    {
        ArgumentNullException.ThrowIfNull(linkId);
        ArgumentNullException.ThrowIfNull(status);

        if (string.IsNullOrWhiteSpace(status))
        {
            throw new ArgumentException("Subscription link status must not be whitespace.", nameof(status));
        }

        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument();
            for (int index = 0; index < document.Links.Count; index++)
            {
                ProfileSubscriptionLink link = document.Links[index];
                if (!StringComparer.Ordinal.Equals(link.Id, linkId))
                {
                    continue;
                }

                document.Links[index] = link with
                {
                    LastUpdatedAt = DateTimeOffset.Now,
                    Status = status.Trim(),
                };
                SaveDocument(document);
                return true;
            }

            return false;
        }
    }

    /// <summary>Checks that a subscription link is reachable without importing it.</summary>
    /// <param name="link">Subscription link to check.</param>
    /// <param name="cancellationToken">Cancels the HTTP request.</param>
    /// <returns>Status message written to the link row.</returns>
    /// <exception cref="HttpRequestException">The subscription endpoint cannot be reached successfully.</exception>
    public async Task<string> CheckSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken)
    {
        try
        {
            EnsureLinkHasHttpUri(link);
            using HttpRequestMessage request = new(HttpMethod.Head, link.Uri);
            using HttpResponseMessage response = await SendWithGetFallbackAsync(request, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            string status = $"检查通过 ({(int)response.StatusCode})";
            TryUpdateSubscriptionLinkStatus(link.Id, status);
            return status;
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or OperationCanceledException or InvalidOperationException)
        {
            TryUpdateSubscriptionLinkStatus(link.Id, "检查失败");
            throw;
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
        try
        {
            EnsureLinkHasHttpUri(link);
            TryUpdateSubscriptionLinkStatus(link.Id, "正在下载");

            string configurationText = await HttpClient.GetStringAsync(new Uri(link.Uri), cancellationToken).ConfigureAwait(false);
            string profileId = $"subscription-{link.Id}";
            ProfileImportResult importResult = await CoreConfigurationService.Instance
                .ImportProfileConfigurationAsync(profileId, link.Name, configurationText, cancellationToken)
                .ConfigureAwait(false);

            lock (_syncLock)
            {
                ProfileCatalogDocument document = LoadDocument();
                UpsertImportedProfile(document, importResult, link.Name);
                UpdateLinkStatus(document, link.Id, "已更新");
                SaveDocument(document);
            }

            return importResult;
        }
        catch (Exception exception) when (exception is ArgumentException or HttpRequestException or InvalidOperationException or IOException or OperationCanceledException)
        {
            TryUpdateSubscriptionLinkStatus(link.Id, cancellationToken.IsCancellationRequested ? "已取消" : "更新失败");
            throw;
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
        ArgumentNullException.ThrowIfNull(filePath);

        if (string.IsNullOrWhiteSpace(filePath))
        {
            throw new ArgumentException("Profile file path must not be whitespace.", nameof(filePath));
        }

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("Profile file was not found.", filePath);
        }

        string configurationText = await File.ReadAllTextAsync(filePath, cancellationToken).ConfigureAwait(false);
        string profileName = Path.GetFileNameWithoutExtension(filePath);
        string profileId = $"local-{Guid.NewGuid():N}";
        ProfileImportResult importResult = await CoreConfigurationService.Instance
            .ImportProfileConfigurationAsync(profileId, profileName, configurationText, cancellationToken)
            .ConfigureAwait(false);

        lock (_syncLock)
        {
            ProfileCatalogDocument document = LoadDocument();
            UpsertImportedProfile(document, importResult, Path.GetFileName(filePath));
            SaveDocument(document);
        }

        return importResult;
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
        if (StringComparer.Ordinal.Equals(profile.Id, ProfileCatalogIds.BuiltInDirect))
        {
            CoreConfigurationState state = CoreConfigurationService.Instance.EnsureDefaultConfiguration();
            ProfileImportResult result = new(profile.Id, profile.Name, state.ConfigPath, 0, 1, "内置直连配置可用。");
            TryUpdateProfileStatus(profile.Id, "可用", result.NodeCount, result.RuleCount);
            return result;
        }

        try
        {
            ProfileImportResult result = await CoreConfigurationService.Instance
                .ValidateImportedProfileAsync(profile.Id, cancellationToken)
                .ConfigureAwait(false);

            TryUpdateProfileStatus(profile.Id, "校验通过", result.NodeCount, result.RuleCount);
            return result with { ProfileName = profile.Name };
        }
        catch
        {
            TryUpdateProfileStatus(profile.Id, cancellationToken.IsCancellationRequested ? "已取消" : "校验失败", profile.NodeCount, profile.RuleCount);
            throw;
        }
    }

    /// <summary>Persists <paramref name="profileId"/> as the active profile when it exists.</summary>
    /// <param name="profileId">Profile identifier to activate. Must not be null.</param>
    /// <returns>True when the profile exists and was activated; otherwise false.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="profileId"/> is null.</exception>
    public bool TrySetActiveProfile(string profileId)
    {
        ArgumentNullException.ThrowIfNull(profileId);

        lock (_syncLock)
        {
            foreach (ConfigurationProfile profile in LoadDocument().Profiles)
            {
                if (StringComparer.Ordinal.Equals(profile.Id, profileId))
                {
                    AppSettingsService.Instance.ActiveProfileId = profileId;
                    return true;
                }
            }

            return false;
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

    /// <summary>Upserts an imported profile row into the catalog document.</summary>
    /// <param name="document">Catalog document to mutate. Must not be null.</param>
    /// <param name="importResult">Import result used for profile metadata.</param>
    /// <param name="sourceName">Profile source display name. Must not be null.</param>
    private static void UpsertImportedProfile(ProfileCatalogDocument document, ProfileImportResult importResult, string sourceName)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(sourceName);

        ConfigurationProfile profile = new(
            importResult.ProfileId,
            importResult.ProfileName,
            sourceName,
            "可用",
            DateTimeOffset.Now,
            importResult.NodeCount,
            importResult.RuleCount,
            false);

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
    private static void UpdateLinkStatus(ProfileCatalogDocument document, string linkId, string status)
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
                LastUpdatedAt = DateTimeOffset.Now,
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
            ProfileCatalogDocument document = LoadDocument();
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
    private static string GetActiveProfileId()
    {
        string activeProfileId = AppSettingsService.Instance.ActiveProfileId;
        return string.IsNullOrWhiteSpace(activeProfileId) ? ProfileCatalogIds.BuiltInDirect : activeProfileId;
    }

    /// <summary>Loads the profile catalog document from disk, creating a default document when needed.</summary>
    /// <returns>Loaded profile catalog document; never null.</returns>
    private ProfileCatalogDocument LoadDocument()
    {
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
                    _cachedDocument = EnsureBuiltInProfile(document);
                    return _cachedDocument;
                }
            }
            catch (JsonException exception)
            {
                LogStorageService.Instance.AppendLog("Warning", "Profiles", "Profile catalog JSON could not be read.", exception.Message);
            }
            catch (IOException exception)
            {
                LogStorageService.Instance.AppendLog("Warning", "Profiles", "Profile catalog file could not be read.", exception.Message);
            }
        }

        _cachedDocument = BuildDefaultDocument();
        SaveDocument(_cachedDocument);
        return _cachedDocument;
    }

    /// <summary>Saves the profile catalog document to disk and updates the in-memory cache.</summary>
    /// <param name="document">Profile catalog document to save. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    private void SaveDocument(ProfileCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        JsonSerializerOptions options = new()
        {
            WriteIndented = true,
        };

        string json = JsonSerializer.Serialize(document, options);
        File.WriteAllText(_catalogPath, json);
        _cachedDocument = document;
    }

    /// <summary>Ensures the catalog document contains the built-in direct profile.</summary>
    /// <param name="document">Catalog document to inspect. Must not be null.</param>
    /// <returns>The original document with the built-in profile inserted when necessary.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="document"/> is null.</exception>
    private static ProfileCatalogDocument EnsureBuiltInProfile(ProfileCatalogDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        document.Profiles ??= [];
        document.Links ??= [];
        RemoveObsoletePreviewProfiles(document.Profiles);

        foreach (ConfigurationProfile profile in document.Profiles)
        {
            if (StringComparer.Ordinal.Equals(profile.Id, ProfileCatalogIds.BuiltInDirect))
            {
                return document;
            }
        }

        document.Profiles.Insert(0, BuildDefaultProfile());
        return document;
    }

    /// <summary>Builds the default catalog document used on first run.</summary>
    /// <returns>A catalog document containing the built-in direct profile and no user links.</returns>
    private static ProfileCatalogDocument BuildDefaultDocument()
    {
        return new ProfileCatalogDocument
        {
            Profiles =
            [
                BuildDefaultProfile(),
            ],
            Links = [],
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
    private static ConfigurationProfile BuildDefaultProfile()
    {
        return new ConfigurationProfile(
            ProfileCatalogIds.BuiltInDirect,
            "本机直连默认配置",
            "Clash#",
            "可用",
            DateTimeOffset.Now,
            0,
            1,
            false);
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
    }
}
