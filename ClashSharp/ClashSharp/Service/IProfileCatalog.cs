using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides complete operations on profiles and subscription updates, without exposing repository ownership.</summary>
/// <remarks>Results are detached values. Consumers must not retain file handles or dispose the underlying generation.</remarks>
internal interface IProfileCatalog
{
    IReadOnlyList<ConfigurationProfile> GetProfiles();

    IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks();

    IReadOnlyList<ProfileHistoryEntry> GetProfileHistory(string profileId);

    IReadOnlyList<ProfileSubscriptionLink> GetDueSubscriptionLinks(DateTimeOffset now);

    Task RecordSubscriptionUpdateOutcomeAsync(string linkId, bool succeeded, DateTimeOffset attemptedAt, CancellationToken cancellationToken);

    ProfileCatalogSummary GetSummary(ProfileCatalogFallbackStrings fallbackStrings, string? activeProfileId = null);

    Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(string name, string uri, CancellationToken cancellationToken);

    Task<bool> TryUpdateSubscriptionLinkAsync(string linkId, string name, string uri, bool isEnabled, int updateIntervalHours, CancellationToken cancellationToken);

    Task<bool> TryDeleteSubscriptionLinkAsync(string linkId, CancellationToken cancellationToken);

    Task<bool> TryRenameProfileAsync(string profileId, string name, CancellationToken cancellationToken);

    Task<bool> TryDeleteProfileAsync(string profileId, CancellationToken cancellationToken);

    Task<bool> TryUpdateSubscriptionLinkStatusAsync(string linkId, string status, CancellationToken cancellationToken);

    Task<string> CheckSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken);

    Task<ProfileImportResult> ImportSubscriptionLinkAsync(ProfileSubscriptionLink link, CancellationToken cancellationToken);

    Task<ProfileImportResult?> ImportDueSubscriptionLinkAsync(ProfileSubscriptionLink link, DateTimeOffset now, CancellationToken cancellationToken);

    Task<ProfileImportResult> ImportLocalProfileAsync(string filePath, CancellationToken cancellationToken);

    Task<ProfileImportResult> RollbackProfileAsync(ProfileHistoryEntry historyEntry, CancellationToken cancellationToken);

    Task<ProfileImportResult> ValidateProfileAsync(ConfigurationProfile profile, CancellationToken cancellationToken);

    Task<bool> TryApplyActiveProfileAsync(string profileId, CancellationToken cancellationToken);

    Task RetryPendingProfileCleanupAsync(CancellationToken cancellationToken);

    /// <summary>Invalidates cached rows after an externally coordinated catalog replacement.</summary>
    void InvalidateCache();
}
