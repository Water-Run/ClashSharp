using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Lifecycle;

/// <summary>Connects awaitable tile commands to the active page's ordered platform interactions.</summary>
/// <remarks>
/// Activation and dispatch belong to the UI thread. Deactivation releases the page callback,
/// cancels accepted work, and leaves its tasks owned until DrainAsync completes.
/// </remarks>
internal sealed class MasterControlTileActionSession
{
    private readonly PageOperationSession _operations;
    private Func<MasterControlTileAction, CancellationToken, Task>? _presentAsync;
    private Func<bool>? _canRun;

    public MasterControlTileActionSession(IApplicationErrorSink errorSink)
    {
        _operations = new PageOperationSession(errorSink, "master-tile-action");
    }

    /// <summary>Attaches the platform callback for a loaded page after its previous work has drained.</summary>
    public void Activate(Func<MasterControlTileAction, CancellationToken, Task> presentAsync, Func<bool>? canRun = null)
    {
        ArgumentNullException.ThrowIfNull(presentAsync);
        if (_presentAsync is not null)
        {
            throw new InvalidOperationException("The master-control page is already active.");
        }

        _presentAsync = presentAsync;
        _canRun = canRun;
    }

    /// <summary>Releases the page callback and cancels every accepted interaction.</summary>
    public void Deactivate()
    {
        _presentAsync = null;
        _canRun = null;
        _operations.Cancel();
    }

    /// <summary>Revokes old data interactions while retaining the visible page's callback.</summary>
    public void Cancel() => _operations.Cancel();

    /// <summary>Executes a tile action and completes only after its platform interaction has unwound.</summary>
    public Task ExecuteAsync(MasterControlTileAction action, CancellationToken cancellationToken)
    {
        Func<MasterControlTileAction, CancellationToken, Task>? presentAsync = _presentAsync;
        return presentAsync is null
            ? Task.CompletedTask
            : RunAsync(token => presentAsync(action, token), cancellationToken);
    }

    /// <summary>Owns an additional page interaction, such as editing the tile layout.</summary>
    public Task RunAsync(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return _presentAsync is null || _canRun?.Invoke() == false
            ? Task.CompletedTask
            : _operations.RunAsync(token => _presentAsync is null || _canRun?.Invoke() == false
                ? Task.CompletedTask : operation(token), cancellationToken);
    }

    /// <summary>Observes the completion of interactions accepted before this call.</summary>
    public Task DrainAsync() => _operations.DrainAsync();
}
