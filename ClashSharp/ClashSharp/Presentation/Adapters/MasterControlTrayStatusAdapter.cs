using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts tray status snapshots to the master-control page.</summary>
internal sealed class MasterControlTrayStatusAdapter : IMasterControlTrayStatus
{
    private readonly TrayStatusService _trayStatus;

    public MasterControlTrayStatusAdapter(TrayStatusService trayStatus)
    {
        _trayStatus = trayStatus ?? throw new ArgumentNullException(nameof(trayStatus));
    }

    public Task<TrayStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        return _trayStatus.GetSnapshotAsync(cancellationToken);
    }
}
