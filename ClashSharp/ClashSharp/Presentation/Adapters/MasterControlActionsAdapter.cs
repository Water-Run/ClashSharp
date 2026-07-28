using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Adapters;

/// <summary>Adapts the application action dispatcher to the master-control action boundary.</summary>
internal sealed class MasterControlActionsAdapter : IMasterControlActions
{
    private readonly IApplicationActionDispatcher _dispatcher;

    public MasterControlActionsAdapter(IApplicationActionDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public Task DispatchAsync(
        ApplicationActionKind kind,
        string value,
        CancellationToken cancellationToken)
    {
        return _dispatcher.DispatchAsync(kind, value, cancellationToken);
    }
}
