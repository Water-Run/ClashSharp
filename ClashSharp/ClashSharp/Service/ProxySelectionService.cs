using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Restores profile choices inside an already-admitted runtime activation transaction.</summary>
internal interface INetworkTakeoverProxySelections
{
    Task RestoreAsync(RuntimeConfigurationActivationPlan plan, CancellationToken cancellationToken);
}

/// <summary>Persists acknowledged selections and reapplies them across App/service owner changes.</summary>
internal sealed partial class ProxySelectionService : INetworkTakeoverProxySelections
{
    private readonly IProxySelectionStore _store;
    private readonly Func<CancellationToken, Task<IReadOnlyList<MihomoProxyGroup>>> _getGroups;
    private readonly Func<string, string, CancellationToken, Task> _select;
    private readonly Func<RuntimeConfigurationIntegrityObservation> _observeRuntime;
    private readonly IProfileCatalogMutationCoordinator _mutations;

    public ProxySelectionService(
        IProxySelectionStore store,
        Func<CancellationToken, Task<IReadOnlyList<MihomoProxyGroup>>> getGroups,
        Func<string, string, CancellationToken, Task> select,
        Func<RuntimeConfigurationIntegrityObservation> observeRuntime,
        IProfileCatalogMutationCoordinator mutations)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _getGroups = getGroups ?? throw new ArgumentNullException(nameof(getGroups));
        _select = select ?? throw new ArgumentNullException(nameof(select));
        _observeRuntime = observeRuntime ?? throw new ArgumentNullException(nameof(observeRuntime));
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
    }

    public Task SelectAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        ProxySelectionStore.ValidateName(groupName);
        ProxySelectionStore.ValidateName(proxyName);
        // Profile activation, import/reset, shutdown, and node choice share admission and ordering.
        return _mutations.ExecuteAsync(
            Guid.NewGuid(),
            (_, token) =>
            {
                return Task.Run(async () =>
                {
                    await SelectCoreAsync(groupName, proxyName, token).ConfigureAwait(false);
                    return true;
                }, token);
            },
            cancellationToken);
    }

    private async Task SelectCoreAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        RuntimeConfigurationIntegrityObservation before = _observeRuntime();
        if (!before.IsKnown || before.AppliedPlan is not { Mode: not ClashSharpMode.Disabled } plan)
        {
            throw new InvalidOperationException("The active proxy profile is unavailable.");
        }

        // Reject a corrupt persistence document before changing the live core.
        _ = _store.Read(plan.ProfileId);
        IReadOnlyList<MihomoProxyGroup> groups = await _getGroups(cancellationToken).ConfigureAwait(false);
        MihomoProxyGroup group = groups.FirstOrDefault(candidate =>
            StringComparer.Ordinal.Equals(candidate.Name, groupName));
        if (group.Name is null || !group.Candidates.Contains(proxyName, StringComparer.Ordinal))
        {
            throw new InvalidOperationException("The requested proxy is not in the current strategy group.");
        }

        if (before != _observeRuntime())
        {
            throw new InvalidOperationException("The active proxy profile changed before selection.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await _select(groupName, proxyName, cancellationToken).ConfigureAwait(false);
            await VerifyAsync(groupName, proxyName, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (before != _observeRuntime())
            {
                throw new InvalidOperationException("The active proxy profile changed during selection.");
            }

            _store.Save(plan.ProfileId, groupName, proxyName);
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            try
            {
                if (before != _observeRuntime())
                {
                    throw new InvalidOperationException("The previous runtime no longer owns the selection.");
                }

                // The failed request may already have reached mihomo. Compensate and verify even
                // for caller cancellation, without allowing cancellation to strand a new choice.
                using CancellationTokenSource rollback = new(TimeSpan.FromSeconds(5));
                await _select(groupName, group.CurrentSelection, rollback.Token).ConfigureAwait(false);
                await VerifyAsync(groupName, group.CurrentSelection, rollback.Token).ConfigureAwait(false);
            }
            catch (Exception rollbackFailure) when (!ExceptionGraphClassifier.IsProcessFatal(rollbackFailure))
            {
                throw new InvalidOperationException("Proxy selection rollback could not be verified.",
                    new AggregateException(failure, rollbackFailure));
            }

            if (failure is OperationCanceledException) { throw; }
            throw new InvalidOperationException("The proxy choice could not be saved; the previous choice was restored.", failure);
        }
    }

    public async Task RestoreAsync(RuntimeConfigurationActivationPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Mode == ClashSharpMode.Disabled) { return; }
        IReadOnlyDictionary<string, string> saved = _store.Read(plan.ProfileId);
        IReadOnlyList<MihomoProxyGroup> groups = await _getGroups(cancellationToken).ConfigureAwait(false);
        Dictionary<string, string> changed = new(StringComparer.Ordinal);
        foreach (MihomoProxyGroup group in groups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // mihomo's per-owner cache keys by group name, not profile. An unsaved selector
            // must use its configured first candidate, never another profile's cached choice.
            if (!saved.TryGetValue(group.Name, out string? selection)
                || !group.Candidates.Contains(selection, StringComparer.Ordinal))
            {
                if (!StringComparer.OrdinalIgnoreCase.Equals(group.Type, "Selector")
                    || group.Candidates.Count == 0)
                {
                    continue;
                }

                selection = group.Candidates[0];
            }

            if (!StringComparer.Ordinal.Equals(group.CurrentSelection, selection))
            {
                await _select(group.Name, selection, cancellationToken).ConfigureAwait(false);
                changed.Add(group.Name, selection);
            }
        }

        if (changed.Count == 0) { return; }
        IReadOnlyList<MihomoProxyGroup> restored = await _getGroups(cancellationToken).ConfigureAwait(false);
        foreach ((string groupName, string selection) in changed)
        {
            if (!restored.Any(group => StringComparer.Ordinal.Equals(group.Name, groupName)
                && StringComparer.Ordinal.Equals(group.CurrentSelection, selection)))
            {
                throw new InvalidOperationException("The controller did not restore a saved proxy choice.");
            }
        }
    }

    private async Task VerifyAsync(string groupName, string proxyName, CancellationToken cancellationToken)
    {
        IReadOnlyList<MihomoProxyGroup> groups = await _getGroups(cancellationToken).ConfigureAwait(false);
        if (!groups.Any(group => StringComparer.Ordinal.Equals(group.Name, groupName)
            && StringComparer.Ordinal.Equals(group.CurrentSelection, proxyName)))
        {
            throw new InvalidOperationException("The controller did not confirm the requested proxy choice.");
        }
    }
}
