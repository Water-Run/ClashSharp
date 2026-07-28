using System.Collections.Generic;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Connection logging contract used by <see cref="ConnectionsViewModel"/>.</summary>
internal interface IConnectionLog
{
    int AppendConnectionSnapshot(IReadOnlyList<ActiveConnection> connections);

    void Append(string level, string category, string message, string? detail);
}
