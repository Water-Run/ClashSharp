using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Runtime proxy group contract required by <see cref="TrayStatusService"/>.</summary>
internal interface ITrayStatusRuntime
{
    /// <summary>Gets routing mode and groups belonging to one verified runtime generation.</summary>
    Task<TrayStatusRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
}

/// <summary>Proxy selections observed without a concurrent runtime configuration transition.</summary>
internal readonly record struct TrayStatusRuntimeSnapshot(
    ClashSharpMode Mode,
    IReadOnlyList<MihomoProxyGroup> Groups);

/// <summary>Node health contract required by <see cref="TrayStatusService"/>.</summary>
internal interface ITrayStatusHealthStorage
{
    /// <summary>Gets latest measured latency for a node.</summary>
    int? GetNodeLatencyMilliseconds(string nodeName);
}

/// <summary>Builds current tray status snapshots.</summary>
/// <remarks>
/// Invariants: Returned snapshots are non-null value types and safe for display.
/// Thread safety: Depends on injected runtime and storage implementations.
/// Side effects: Reads runtime proxy state and local node health storage.
/// </remarks>
public sealed partial class TrayStatusService
{
    private readonly ITrayStatusRuntime _runtime;

    private readonly ITrayStatusHealthStorage _healthStorage;

    private readonly Func<string, string> _filterText;

    private readonly object _snapshotLock = new();

    private TrayStatusSnapshot _latestSnapshot = TrayStatusSnapshot.Unavailable;

    /// <summary>Initializes a tray status service.</summary>
    internal TrayStatusService(
        ITrayStatusRuntime runtime,
        ITrayStatusHealthStorage healthStorage,
        Func<string, string> filterText)
    {
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _healthStorage = healthStorage ?? throw new ArgumentNullException(nameof(healthStorage));
        _filterText = filterText ?? throw new ArgumentNullException(nameof(filterText));
    }

    /// <summary>Gets a best-effort snapshot of current tray status details.</summary>
    /// <returns>Current status, or <see cref="TrayStatusSnapshot.Unavailable"/> when runtime state is unavailable.</returns>
    public async Task<TrayStatusSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        TrayStatusSnapshot snapshot;
        try
        {
            TrayStatusRuntimeSnapshot runtime = await _runtime
                .GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            string? nodeName = ResolveCurrentNode(runtime);
            if (string.IsNullOrWhiteSpace(nodeName))
            {
                snapshot = TrayStatusSnapshot.Unavailable;
            }
            else
            {
                snapshot = new TrayStatusSnapshot(
                    _filterText(nodeName),
                    nodeName is "DIRECT" or "REJECT" or "REJECT-DROP"
                        ? null
                        : _healthStorage.GetNodeLatencyMilliseconds(nodeName));
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            snapshot = TrayStatusSnapshot.Unavailable;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TimeoutException or System.Net.Http.HttpRequestException or System.Text.Json.JsonException)
        {
            snapshot = TrayStatusSnapshot.Unavailable;
        }

        lock (_snapshotLock)
        {
            _latestSnapshot = snapshot;
            return snapshot;
        }
    }

    /// <summary>Gets the latest completed snapshot without performing controller I/O.</summary>
    public TrayStatusSnapshot GetLatestSnapshot()
    {
        lock (_snapshotLock)
        {
            return _latestSnapshot;
        }
    }

    private static string? ResolveCurrentNode(TrayStatusRuntimeSnapshot runtime)
    {
        if (runtime.Mode == ClashSharpMode.Standby)
        {
            return "DIRECT";
        }

        if (runtime.Mode is not (ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover))
        {
            return null;
        }

        MihomoProxyGroup? selected = SelectPrimaryGroup(runtime.Groups, runtime.Mode);
        HashSet<string> visited = new(StringComparer.Ordinal);
        while (selected is MihomoProxyGroup group)
        {
            if (!visited.Add(group.Name) || string.IsNullOrWhiteSpace(group.CurrentSelection))
            {
                return null;
            }

            string selection = group.CurrentSelection;
            selected = null;
            foreach (MihomoProxyGroup candidate in runtime.Groups)
            {
                if (StringComparer.Ordinal.Equals(candidate.Name, selection))
                {
                    selected = candidate;
                    break;
                }
            }

            if (selected is null)
            {
                return selection;
            }
        }

        return null;
    }

    private static MihomoProxyGroup? SelectPrimaryGroup(
        IReadOnlyList<MihomoProxyGroup> groups,
        ClashSharpMode mode)
    {
        MihomoProxyGroup? firstSelectableGroup = null;
        foreach (MihomoProxyGroup group in groups)
        {
            bool isGlobal = StringComparer.Ordinal.Equals(group.Name, "GLOBAL");
            if (mode == ClashSharpMode.FullTakeover)
            {
                if (isGlobal) { return group; }
                continue;
            }

            // GLOBAL is inactive in rule mode and must not become a fallback status source.
            if (isGlobal) { continue; }
            if (string.IsNullOrWhiteSpace(group.CurrentSelection))
            {
                continue;
            }

            firstSelectableGroup ??= group;
            if (StringComparer.OrdinalIgnoreCase.Equals(group.Name, "Proxy"))
            {
                return group;
            }
        }

        return firstSelectableGroup;
    }
}
