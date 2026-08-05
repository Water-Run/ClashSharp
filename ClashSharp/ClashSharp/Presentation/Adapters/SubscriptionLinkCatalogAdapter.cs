using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts profile persistence to the links presentation contract.</summary>
internal sealed class SubscriptionLinkCatalogAdapter : ISubscriptionLinkCatalog
{
    private readonly ProfileCatalogService _catalog;

    public SubscriptionLinkCatalogAdapter(ProfileCatalogService catalog)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    }

    public IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks()
    {
        return _catalog.GetSubscriptionLinks();
    }

    public Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(
        string name,
        string uri,
        CancellationToken cancellationToken)
    {
        return _catalog.AddSubscriptionLinkAsync(name, uri, cancellationToken);
    }

    public Task<string> CheckSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken)
    {
        return _catalog.CheckSubscriptionLinkAsync(link, cancellationToken);
    }

    public Task<bool> TryUpdateSubscriptionLinkAsync(
        SubscriptionLinkEditRequest request,
        CancellationToken cancellationToken)
    {
        return _catalog.TryUpdateSubscriptionLinkAsync(
            request.LinkId,
            request.Name,
            request.Uri,
            request.IsEnabled,
            request.UpdateIntervalHours,
            cancellationToken);
    }

    public Task<bool> TryDeleteSubscriptionLinkAsync(
        string linkId,
        CancellationToken cancellationToken)
    {
        return _catalog.TryDeleteSubscriptionLinkAsync(linkId, cancellationToken);
    }

    public Task<ProfileImportResult> ImportSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken)
    {
        return _catalog.ImportSubscriptionLinkAsync(link, cancellationToken);
    }
}
