using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Presentation;

namespace ClashSharp.Presentation.Lifecycle;

/// <summary>Revokes old page work immediately and owns a queued refresh for the current visible visit.</summary>
internal sealed class PageDataChangeSession
{
    private readonly Func<Action, IDisposable> _subscribe;
    private readonly Func<Action, bool> _dispatch;
    private readonly Action[] _cancelWork;
    private readonly Func<CancellationToken, Task> _reload;
    private readonly PageOperationSession _refresh;
    private readonly PageOperationSession _errors;
    private IDisposable? _subscription;
    private int _visit;
    private int _revision;
    private int _active;
    private int _invalidated;

    public PageDataChangeSession(Func<Action, IDisposable> subscribe, Func<Action, bool> dispatch,
        IEnumerable<Action> cancelWork, Func<CancellationToken, Task> reload, IApplicationErrorSink errors, string operationName)
    {
        _subscribe = subscribe ?? throw new ArgumentNullException(nameof(subscribe));
        _dispatch = dispatch ?? throw new ArgumentNullException(nameof(dispatch));
        ArgumentNullException.ThrowIfNull(cancelWork);
        _cancelWork = [.. cancelWork];
        _reload = reload ?? throw new ArgumentNullException(nameof(reload));
        _refresh = new(errors, operationName);
        _errors = new(errors, operationName + "-cancel");
    }

    public bool IsInvalidated => Volatile.Read(ref _invalidated) != 0;

    public void Start()
    {
        if (Interlocked.Exchange(ref _active, 1) != 0) { return; }
        int visit = Interlocked.Increment(ref _visit);
        Volatile.Write(ref _invalidated, 0);
        _subscription = _subscribe(() => Changed(visit));
    }

    public void Stop()
    {
        Volatile.Write(ref _active, 0);
        Interlocked.Increment(ref _visit);
        Interlocked.Exchange(ref _subscription, null)?.Dispose();
        _refresh.Cancel();
    }

    public Task DrainAsync() => Task.WhenAll(_refresh.DrainAsync(), _errors.DrainAsync());

    private bool IsCurrent(int visit) => Volatile.Read(ref _active) != 0 && Volatile.Read(ref _visit) == visit;

    private void Changed(int visit)
    {
        if (!IsCurrent(visit)) { return; }
        int revision = Interlocked.Increment(ref _revision);
        Volatile.Write(ref _invalidated, 1);
        _refresh.Cancel();
        foreach (Action cancel in _cancelWork)
        {
            try { cancel(); }
            catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
            {
                // The error queue retains ownership and is included in page drain.
                _errors.RunAsync(_ => Task.FromException(failure));
            }
        }
        _dispatch(() =>
        {
            if (!IsCurrent(visit)) { return; }
            // PageOperationSession owns the returned task and serializes refreshes.
            _refresh.RunAsync(async token =>
            {
                if (!IsCurrent(visit) || Volatile.Read(ref _revision) != revision) { return; }
                await _reload(token);
                if (IsCurrent(visit) && Volatile.Read(ref _revision) == revision)
                {
                    Volatile.Write(ref _invalidated, 0);
                }
            });
        });
    }
}
