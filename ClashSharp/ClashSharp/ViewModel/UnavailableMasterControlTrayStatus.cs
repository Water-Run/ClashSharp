using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Fallback tray status provider used in tests and when runtime status is unavailable.</summary>
internal sealed class UnavailableMasterControlTrayStatus : IMasterControlTrayStatus
{
    public static UnavailableMasterControlTrayStatus Instance { get; } = new();

    public Task<TrayStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(TrayStatusSnapshot.Unavailable);
    }
}
