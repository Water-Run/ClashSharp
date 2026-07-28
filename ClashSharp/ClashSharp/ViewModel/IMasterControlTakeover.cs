using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Network takeover contract required by <see cref="MasterControlViewModel"/>.</summary>
internal interface IMasterControlTakeover
{
    /// <summary>Applies a master takeover mode.</summary>
    Task<NetworkTakeoverResult> ApplyModeAsync(ClashSharpMode mode, CancellationToken cancellationToken);
}
