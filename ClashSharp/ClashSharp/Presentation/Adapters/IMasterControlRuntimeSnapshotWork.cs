using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Per-request, background-safe work used to aggregate one runtime snapshot.</summary>
internal interface IMasterControlRuntimeSnapshotWork
{
    Task<MasterControlRuntimeSnapshot> ExecuteAsync(CancellationToken cancellationToken);
}
