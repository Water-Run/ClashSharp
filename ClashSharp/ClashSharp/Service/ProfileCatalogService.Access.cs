using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class ProfileCatalogService
{
    void IProfileCatalog.InvalidateCache()
    {
        ResetAfterDataDeletion();
    }

    Task IProfileCatalog.RecordSubscriptionUpdateOutcomeAsync(string linkId, bool succeeded, DateTimeOffset attemptedAt, CancellationToken cancellationToken)
    {
        return RecordSubscriptionUpdateOutcomeAsync(linkId, succeeded, attemptedAt, cancellationToken);
    }

    ProfileCatalogSummary IProfileCatalog.GetSummary(ProfileCatalogFallbackStrings fallbackStrings, string? activeProfileId)
    {
        return GetSummary(fallbackStrings, activeProfileId);
    }

    Task<bool> IProfileCatalog.TryUpdateSubscriptionLinkAsync(string linkId, string name, string uri, bool isEnabled, int updateIntervalHours, CancellationToken cancellationToken)
    {
        return TryUpdateSubscriptionLinkAsync(linkId, name, uri, isEnabled, updateIntervalHours, cancellationToken);
    }

    Task<bool> IProfileCatalog.TryDeleteSubscriptionLinkAsync(string linkId, CancellationToken cancellationToken)
    {
        return TryDeleteSubscriptionLinkAsync(linkId, cancellationToken);
    }

    Task<bool> IProfileCatalog.TryUpdateSubscriptionLinkStatusAsync(string linkId, string status, CancellationToken cancellationToken)
    {
        return TryUpdateSubscriptionLinkStatusAsync(linkId, status, cancellationToken);
    }

    Task<ProfileImportResult?> IProfileCatalog.ImportDueSubscriptionLinkAsync(ProfileSubscriptionLink link, DateTimeOffset now, CancellationToken cancellationToken)
    {
        return ImportDueSubscriptionLinkAsync(link, now, cancellationToken);
    }

    Task IProfileCatalog.RetryPendingProfileCleanupAsync(CancellationToken cancellationToken)
    {
        return RetryPendingProfileCleanupAsync(cancellationToken);
    }
}
