using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Persists the configurable master-control hero status layout.</summary>
internal interface IMasterHeroStatusLayoutService
{
    IReadOnlyList<MasterHeroStatusItemKind> GetLayout();

    IReadOnlyList<MasterHeroStatusItemKind> GetDefaultLayout();

    IReadOnlyList<MasterHeroStatusItemKind> GetCandidates();

    Task<IReadOnlyList<MasterHeroStatusItemKind>> SaveLayoutAsync(IEnumerable<MasterHeroStatusItemKind> layout, CancellationToken cancellationToken);

    Task<IReadOnlyList<MasterHeroStatusItemKind>> ResetLayoutAsync(CancellationToken cancellationToken);
}
