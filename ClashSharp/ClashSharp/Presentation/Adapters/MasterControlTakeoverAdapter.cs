using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts shared application actions to master-control mode application.</summary>
/// <remarks>
/// Invariants: Wraps a non-null takeover service for the adapter lifetime.
/// Thread safety: Matches the wrapped service.
/// Side effects: Applies runtime mode through the wrapped service.
/// </remarks>
internal sealed class MasterControlTakeoverAdapter : IMasterControlTakeover
{
    /// <summary>Shared durable application action service.</summary>
    private readonly ApplicationActionService _actions;

    /// <summary>Initializes a master-control takeover adapter.</summary>
    /// <param name="actions">Application actions. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="actions"/> is null.</exception>
    public MasterControlTakeoverAdapter(ApplicationActionService actions)
    {
        _actions = actions ?? throw new ArgumentNullException(nameof(actions));
    }

    /// <summary>Applies a master takeover mode.</summary>
    /// <param name="mode">Mode to apply.</param>
    /// <param name="cancellationToken">Cancels admission or pre-side-effect work.</param>
    /// <returns>Verified result describing the applied runtime state.</returns>
    public Task<NetworkTakeoverResult> ApplyModeAsync(
        ClashSharpMode mode,
        CancellationToken cancellationToken)
    {
        return _actions.ApplyNetworkModeAsync(mode, cancellationToken);
    }
}
