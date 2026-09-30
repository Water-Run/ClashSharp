using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.ViewModel;

/// <summary>Persists the ordered set of visible master-control information tiles.</summary>
internal interface IMasterInfoTileLayoutService
{
    IReadOnlyList<string> GetLayout(IReadOnlyCollection<string> availableTileIds);

    IReadOnlyList<string> GetRecommendedLayout(IReadOnlyCollection<string> availableTileIds);

    Task<IReadOnlyList<string>> SaveLayoutAsync(
        IEnumerable<string> tileIds,
        IReadOnlyCollection<string> availableTileIds,
        CancellationToken cancellationToken);
}
