using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Tray status contract required by the master-control page.</summary>
internal interface IMasterControlTrayStatus
{
    /// <summary>Gets current node and latency status.</summary>
    Task<TrayStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}
