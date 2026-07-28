using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts <see cref="MihomoCoreService"/> to master-control core probing.</summary>
/// <remarks>
/// Invariants: Wraps a non-null core service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Version probing may start a short-lived mihomo process.
/// </remarks>
internal sealed class MasterControlCoreAdapter : IMasterControlCore
{
    /// <summary>Wrapped core service.</summary>
    private readonly MihomoCoreService _core;

    /// <summary>Initializes a master-control core adapter.</summary>
    /// <param name="core">Core service. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="core"/> is null.</exception>
    public MasterControlCoreAdapter(MihomoCoreService core)
    {
        _core = core ?? throw new ArgumentNullException(nameof(core));
    }

    /// <summary>Gets the bundled core version text.</summary>
    /// <param name="cancellationToken">Cancels the version probe when requested.</param>
    /// <returns>The first user-facing version line returned by the core.</returns>
    /// <remarks>
    /// Cancellation semantics: Passed through to the wrapped service.
    /// Completion semantics: Does not mutate long-running core state.
    /// </remarks>
    public Task<string> GetVersionTextAsync(CancellationToken cancellationToken)
    {
        return _core.GetVersionTextAsync(cancellationToken);
    }
}
