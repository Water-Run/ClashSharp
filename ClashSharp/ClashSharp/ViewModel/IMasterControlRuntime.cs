using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.ViewModel;

/// <summary>Runtime information contract required by the master-control tile catalog.</summary>
internal interface IMasterControlRuntime
{
    Task<MasterControlRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
