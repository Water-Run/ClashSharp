using System.Collections.Generic;

namespace ClashSharp.ViewModel;

/// <summary>Persists the ordered set of visible master-control information tiles.</summary>
internal interface IMasterInfoTileLayoutService
{
    IReadOnlyList<string> GetLayout(IReadOnlyCollection<string> availableTileIds);

    IReadOnlyList<string> SaveLayout(
        IEnumerable<string> tileIds,
        IReadOnlyCollection<string> availableTileIds);
}
