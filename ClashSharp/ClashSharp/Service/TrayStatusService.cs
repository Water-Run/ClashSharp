using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Runtime proxy group contract required by <see cref="TrayStatusService"/>.</summary>
internal interface ITrayStatusRuntime
{
    /// <summary>Gets current runtime proxy groups.</summary>
    Task<IReadOnlyList<MihomoProxyGroup>> GetProxyGroupsAsync(CancellationToken cancellationToken);
}

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
            IReadOnlyList<MihomoProxyGroup> groups = await _runtime
                .GetProxyGroupsAsync(cancellationToken)
                .ConfigureAwait(false);
            MihomoProxyGroup? primaryGroup = SelectPrimaryGroup(groups);
            if (primaryGroup is not MihomoProxyGroup group || string.IsNullOrWhiteSpace(group.CurrentSelection))
            {
                snapshot = TrayStatusSnapshot.Unavailable;
            }
            else
            {
                string nodeName = group.CurrentSelection.Trim();
                snapshot = new TrayStatusSnapshot(
                    _filterText(nodeName),
                    _healthStorage.GetNodeLatencyMilliseconds(nodeName));
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

    private static MihomoProxyGroup? SelectPrimaryGroup(IReadOnlyList<MihomoProxyGroup> groups)
    {
        MihomoProxyGroup? firstSelectableGroup = null;
        foreach (MihomoProxyGroup group in groups)
        {
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
