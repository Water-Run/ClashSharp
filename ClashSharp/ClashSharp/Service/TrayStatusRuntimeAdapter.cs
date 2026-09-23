using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Pairs authenticated controller groups with their verified routing mode.</summary>
internal sealed class TrayStatusRuntimeAdapter : ITrayStatusRuntime
{
    private static readonly TimeSpan RuntimeStatusTimeout = TimeSpan.FromMilliseconds(800);
    private readonly Func<CancellationToken, Task<IReadOnlyList<MihomoProxyGroup>>> _getGroups;
    private readonly Func<RuntimeConfigurationIntegrityObservation> _observeRuntime;

    public TrayStatusRuntimeAdapter(
        Func<CancellationToken, Task<IReadOnlyList<MihomoProxyGroup>>> getGroups,
        Func<RuntimeConfigurationIntegrityObservation> observeRuntime)
    {
        _getGroups = getGroups ?? throw new ArgumentNullException(nameof(getGroups));
        _observeRuntime = observeRuntime ?? throw new ArgumentNullException(nameof(observeRuntime));
    }

    public async Task<TrayStatusRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RuntimeConfigurationIntegrityObservation before = await Task
            .Run(_observeRuntime, cancellationToken).ConfigureAwait(false);
        if (!before.IsKnown)
        {
            throw new InvalidOperationException("The runtime configuration is not converged.");
        }

        ClashSharpMode mode = before.AppliedPlan?.Mode ?? ClashSharpMode.Disabled;
        if (mode is ClashSharpMode.Disabled or ClashSharpMode.Faulted)
        {
            return new TrayStatusRuntimeSnapshot(mode, []);
        }

        using CancellationTokenSource timeout = new(RuntimeStatusTimeout);
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeout.Token);
        IReadOnlyList<MihomoProxyGroup> groups = await _getGroups(linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        // A desired preference can lead the running core. Compare the complete applied stamp
        // around controller I/O so an old group's selection cannot describe a new mode/profile.
        RuntimeConfigurationIntegrityObservation after = await Task
            .Run(_observeRuntime, linked.Token).ConfigureAwait(false);
        if (before != after)
        {
            throw new InvalidOperationException("The runtime configuration changed during status sampling.");
        }

        return new TrayStatusRuntimeSnapshot(mode, groups);
    }
}
