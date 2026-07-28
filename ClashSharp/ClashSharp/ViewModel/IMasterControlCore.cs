using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.ViewModel;

/// <summary>Core runtime contract required by <see cref="MasterControlViewModel"/>.</summary>
internal interface IMasterControlCore
{
    /// <summary>Gets the bundled core version text.</summary>
    Task<string> GetVersionTextAsync(CancellationToken cancellationToken);
}
