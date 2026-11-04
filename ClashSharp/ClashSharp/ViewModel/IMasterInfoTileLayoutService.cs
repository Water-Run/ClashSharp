using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Settings;

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

    /// <summary>Gets the saved layout template list in display order.</summary>
    IReadOnlyList<MasterInfoTilePresetEntry> GetPresets();

    /// <summary>Persists the layout template list in the given order.</summary>
    Task<IReadOnlyList<MasterInfoTilePresetEntry>> SavePresetsAsync(
        IEnumerable<MasterInfoTilePresetEntry> entries,
        CancellationToken cancellationToken);

    /// <summary>Restores the built-in layout template list.</summary>
    Task<IReadOnlyList<MasterInfoTilePresetEntry>> RestoreDefaultPresetsAsync(CancellationToken cancellationToken);
}
