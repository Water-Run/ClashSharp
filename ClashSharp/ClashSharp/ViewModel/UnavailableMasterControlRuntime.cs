using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.ViewModel;

/// <summary>Fallback runtime provider used when counters are unavailable.</summary>
internal sealed class UnavailableMasterControlRuntime : IMasterControlRuntime
{
    public static UnavailableMasterControlRuntime Instance { get; } = new();

    public Task<MasterControlRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(MasterControlRuntimeSnapshot.Unavailable);
    }
}
