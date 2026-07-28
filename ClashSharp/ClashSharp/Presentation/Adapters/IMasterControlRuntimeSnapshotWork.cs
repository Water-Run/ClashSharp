using System.Threading;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Per-request, background-safe work used to aggregate one runtime snapshot.</summary>
internal interface IMasterControlRuntimeSnapshotWork
{
    MasterControlRuntimeSnapshot Execute(CancellationToken cancellationToken);
}
