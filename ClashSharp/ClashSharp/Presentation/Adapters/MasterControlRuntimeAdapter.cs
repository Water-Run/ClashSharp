using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ViewModel;
using Microsoft.Data.Sqlite;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts a two-phase runtime snapshot source to master-control summary tiles.</summary>
internal sealed class MasterControlRuntimeAdapter : IMasterControlRuntime
{
    private readonly IMasterControlRuntimeSnapshotSource _source;

    internal MasterControlRuntimeAdapter(IMasterControlRuntimeSnapshotSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public async Task<MasterControlRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IMasterControlRuntimeSnapshotWork work = _source.Capture();
        try
        {
            return await Task.Run(
                    () => work.Execute(cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SqliteException exception)
        {
            throw new MasterControlRuntimeUnavailableException(exception);
        }
    }
}
