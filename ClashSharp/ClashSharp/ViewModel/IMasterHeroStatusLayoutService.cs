using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Persists the configurable master-control hero status layout.</summary>
internal interface IMasterHeroStatusLayoutService
{
    IReadOnlyList<MasterHeroStatusItemKind> GetLayout();

    IReadOnlyList<MasterHeroStatusItemKind> GetDefaultLayout();

    IReadOnlyList<MasterHeroStatusItemKind> GetCandidates();

    IReadOnlyList<MasterHeroStatusItemKind> SaveLayout(IEnumerable<MasterHeroStatusItemKind> layout);

    IReadOnlyList<MasterHeroStatusItemKind> ResetLayout();
}
