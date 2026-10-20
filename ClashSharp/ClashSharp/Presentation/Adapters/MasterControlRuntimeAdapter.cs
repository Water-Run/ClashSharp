using System;
using System.ComponentModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ViewModel;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts a two-phase runtime snapshot source to master-control summary tiles.</summary>
internal sealed class MasterControlRuntimeAdapter : IMasterControlRuntime
{
    private readonly IMasterControlRuntimeSnapshotSource _source;
    private readonly INetworkStateObserver? _networkState;

    internal MasterControlRuntimeAdapter(IMasterControlRuntimeSnapshotSource source, INetworkStateObserver? networkState = null)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _networkState = networkState;
    }

    public async Task<MasterControlRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IMasterControlRuntimeSnapshotWork work = _source.Capture();
        try
        {
            return await Task.Run(
                    async () =>
                    {
                        Task<MasterControlRuntimeSnapshot> summary = work.ExecuteAsync(cancellationToken);
                        Task<NetworkStateSnapshot?> network = ReadNetworkStateAsync(cancellationToken);
                        await Task.WhenAll(summary, network).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();
                        return (await summary.ConfigureAwait(false)) with { NetworkState = await network.ConfigureAwait(false) };
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new MasterControlRuntimeUnavailableException(exception);
        }
    }

    /// <summary>Bounds live verification while keeping a failed observation separate from a saved target.</summary>
    private async Task<NetworkStateSnapshot?> ReadNetworkStateAsync(CancellationToken cancellationToken)
    {
        if (_networkState is null) { return null; }
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            NetworkStateSnapshot state = await _networkState.ObserveAsync(deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return state.IsKnown ? state : null;
        }
        catch (Exception exception) when (
            exception is InvalidOperationException or IOException or UnauthorizedAccessException
                or HttpRequestException or JsonException or TimeoutException or OperationCanceledException or Win32Exception
            && !ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            return null;
        }
    }
}
