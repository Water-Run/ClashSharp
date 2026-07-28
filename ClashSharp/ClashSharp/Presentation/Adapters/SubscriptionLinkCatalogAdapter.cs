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
        return Task.Run(
            () => _catalog.AddSubscriptionLink(name, uri),
            cancellationToken);
    }

    public Task<string> CheckSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken)
    {
        return _catalog.CheckSubscriptionLinkAsync(link, cancellationToken);
    }

    public Task<ProfileImportResult> ImportSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken)
    {
        return _catalog.ImportSubscriptionLinkAsync(link, cancellationToken);
    }
}
