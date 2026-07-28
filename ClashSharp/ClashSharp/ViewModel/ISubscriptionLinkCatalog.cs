using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Subscription operations required by the links presentation model.</summary>
internal interface ISubscriptionLinkCatalog
{
    IReadOnlyList<ProfileSubscriptionLink> GetSubscriptionLinks();

    Task<ProfileSubscriptionLink> AddSubscriptionLinkAsync(
        string name,
        string uri,
        CancellationToken cancellationToken);

    Task<string> CheckSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken);

    Task<ProfileImportResult> ImportSubscriptionLinkAsync(
        ProfileSubscriptionLink link,
        CancellationToken cancellationToken);
}
