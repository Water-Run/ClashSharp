using System.Diagnostics.CodeAnalysis;

namespace ClashSharp.ApplicationModel.Startup;

/// <summary>Coordinates startup-shell completion with an in-flight lifetime request.</summary>
/// <typeparam name="TContext">Runtime context required to complete the startup shell.</typeparam>
public sealed class StartupCompletionGate<TContext>
    where TContext : class
{
    private readonly object _syncLock = new();
    private TContext? _pending;
    private StartupCompletionState _state;

    /// <summary>Accepts a startup context when the shell startup step becomes ready.</summary>
    public bool TryAccept(
        TContext context,
        bool hasAcceptedLifetimeRequest,
        [NotNullWhen(true)] out TContext? completion)
    {
        ArgumentNullException.ThrowIfNull(context);
        lock (_syncLock)
        {
            if (_state is StartupCompletionState.Completed or StartupCompletionState.Abandoned)
            {
                completion = null;
                return false;
            }

            if (hasAcceptedLifetimeRequest)
            {
                _pending = context;
                completion = null;
                return false;
            }

            _pending = null;
            _state = StartupCompletionState.Completed;
            completion = context;
            return true;
        }
    }

    /// <summary>Attempts to resume a previously deferred startup completion.</summary>
    public bool TryResume(
        bool hasAcceptedLifetimeRequest,
        bool isHostAttached,
        [NotNullWhen(true)] out TContext? completion)
    {
        lock (_syncLock)
        {
            if (_state != StartupCompletionState.Waiting
                || _pending is null
                || hasAcceptedLifetimeRequest
                || !isHostAttached)
            {
                completion = null;
                return false;
            }

            completion = _pending;
            _pending = null;
            _state = StartupCompletionState.Completed;
            return true;
        }
    }

    /// <summary>Discards any deferred startup completion after a terminal lifetime outcome.</summary>
    public void Abandon()
    {
        lock (_syncLock)
        {
            _pending = null;
            _state = StartupCompletionState.Abandoned;
        }
    }

    private enum StartupCompletionState
    {
        Waiting,
        Completed,
        Abandoned,
    }
}
