using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Resolves the current repository for every call and retains its generation until that operation finishes.</summary>
internal sealed class GenerationProfileCatalog(DataGenerationManager generations) : IProfileCatalog
{
    private readonly DataGenerationManager _generations = generations ?? throw new ArgumentNullException(nameof(generations));

    public void InvalidateCache()
    {
        _generations.Execute<IProfileCatalog>((storage, _) => storage.InvalidateCache());
    }

    public IReadOnlyList<ConfigurationProfile> GetProfiles()
    {
        return _generations.Execute<IProfileCatalog, IReadOnlyList<ConfigurationProfile>>((storage, _) => storage.GetProfiles());
    }

    public IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks()
    {
        return _generations.Execute<IProfileCatalog, IReadOnlyList<ProfileSubscriptionLink>>((storage, _) => storage.GetSubscriptionLinks());
    }

    public IReadOnlyList<ProfileHistoryEntry> GetProfileHistory(string profileId)
    {
        return _generations.Execute<IProfileCatalog, IReadOnlyList<ProfileHistoryEntry>>((storage, _) => storage.GetProfileHistory(profileId));
    }

    public IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now)
    {
        return _generations.Execute<IProfileCatalog, IReadOnlyList<ProfileSubscriptionLink>>((storage, _) => storage.GetDueSubscriptionLinks(now));
    }

    public Task RecordSubscriptionUpdateOutcomeAsync(string linkId, bool succeeded, DateTimeOffset attemptedAt, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog>(
            (storage, _, token) => storage.RecordSubscriptionUpdateOutcomeAsync(linkId, succeeded, attemptedAt, token), cancellationToken);
    }

    public ProfileCatalogSummary GetSummary(ProfileCatalogFallbackStrings fallbackStrings, string? activeProfileId = null)
    {
        return _generations.Execute<IProfileCatalog, ProfileCatalogSummary>((storage, _) => storage.GetSummary(fallbackStrings, activeProfileId));
    }

    public Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(string name, string uri, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, ProfileSubscriptionLink>(
            (storage, _, token) => storage.AddSubscriptionLinkAsync(name, uri, token), cancellationToken);
    }

    public Task<bool> TryUpdateSubscriptionLinkAsync(string linkId, string name, string uri, bool isEnabled, int updateIntervalHours, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, bool>(
            (storage, _, token) => storage.TryUpdateSubscriptionLinkAsync(linkId, name, uri, isEnabled, updateIntervalHours, token), cancellationToken);
    }

    public Task<bool> TryDeleteSubscriptionLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, bool>(
            (storage, _, token) => storage.TryDeleteSubscriptionLinkAsync(linkId, token), cancellationToken);
    }

    public Task<bool> TryRenameProfileAsync(string profileId, string name, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, bool>(
            (storage, _, token) => storage.TryRenameProfileAsync(profileId, name, token), cancellationToken);
    }

    public Task<bool> TryDeleteProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, bool>(
            (storage, _, token) => storage.TryDeleteProfileAsync(profileId, token), cancellationToken);
    }

    public Task<bool> TryUpdateSubscriptionLinkStatusAsync(string linkId, string status, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, bool>(
            (storage, _, token) => storage.TryUpdateSubscriptionLinkStatusAsync(linkId, status, token), cancellationToken);
    }

    public Task<string> CheckSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, string>(
            (storage, _, token) => storage.CheckSubscriptionLinkAsync(link, token), cancellationToken);
    }

    public Task<ProfileImportResult> ImportSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, ProfileImportResult>(
            (storage, _, token) => storage.ImportSubscriptionLinkAsync(link, token), cancellationToken);
    }

    public Task<ProfileImportResult?> ImportDueSubscriptionLinkAsync(ProfileSubscriptionLink link, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, ProfileImportResult?>(
            (storage, _, token) => storage.ImportDueSubscriptionLinkAsync(link, now, token), cancellationToken);
    }

    public Task<ProfileImportResult> ImportLocalProfileAsync(string filePath, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, ProfileImportResult>(
            (storage, _, token) => storage.ImportLocalProfileAsync(filePath, token), cancellationToken);
    }

    public Task<ProfileImportResult> RollbackProfileAsync(ProfileHistoryEntry historyEntry, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, ProfileImportResult>(
            (storage, _, token) => storage.RollbackProfileAsync(historyEntry, token), cancellationToken);
    }

    public Task<ProfileImportResult> ValidateProfileAsync(ConfigurationProfile profile, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, ProfileImportResult>(
            (storage, _, token) => storage.ValidateProfileAsync(profile, token), cancellationToken);
    }

    public Task<bool> TryApplyActiveProfileAsync(string profileId, CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog, bool>(
            (storage, _, token) => storage.TryApplyActiveProfileAsync(profileId, token), cancellationToken);
    }

    public Task RetryPendingProfileCleanupAsync(CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<IProfileCatalog>(
            (storage, _, token) => storage.RetryPendingProfileCleanupAsync(token), cancellationToken);
    }
}
