using System.Diagnostics;
using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>
/// Owns one UI-thread window-state load and explicit save checkpoints. The host drains this session
/// before disposing storage or clearing local data; it starts no timers or unobserved background work.
/// </summary>
public sealed class WindowPlacementSession
{
    private readonly IWindowPlacementStore _store;
    private readonly IWindowPlacementView _view;
    private readonly IApplicationErrorSink _errors;
    private Task? _initialization;
    private Task _checkpoint = Task.CompletedTask;
    private bool _initialized;

    /// <summary>Whether shutdown admission prevents new geometry writes.</summary>
    public bool IsStopping { get; private set; }

    /// <summary>Creates a session without file access or window changes.</summary>
    public WindowPlacementSession(IWindowPlacementStore store, IWindowPlacementView view, IApplicationErrorSink errors)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _errors = errors ?? throw new ArgumentNullException(nameof(errors));
    }

    /// <summary>Loads at most once and applies placement on the caller's UI context.</summary>
    public Task InitializeAsync(CancellationToken cancellationToken) => _initialization ??= InitializeCoreAsync(cancellationToken);

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            WindowPlacementState? before = _view.Capture();
            WindowPlacementState? state = await _store.LoadAsync(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Explicit movement while the file was loading takes precedence over old placement.
            if (state is not null && (before is null || _view.Capture() == before)) { state.Validate(); _view.Restore(state); }
            _initialized = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            // A corrupt or inaccessible placement must not prevent opening the application.
            _initialized = true;
            await ReportAsync("window-placement-load", exception);
        }
    }

    /// <summary>Drains initialization and optionally saves, retaining minimized/hidden history.</summary>
    public Task CheckpointAsync(bool save, CancellationToken cancellationToken) => IsStopping ? _checkpoint
        : _checkpoint = CheckpointCoreAsync(_checkpoint, save, cancellationToken);

    /// <summary>Stops admitting new saves, drains earlier work, and optionally saves the final geometry.</summary>
    public Task PrepareShutdownAsync(bool save, CancellationToken cancellationToken)
    {
        IsStopping = true;
        return _checkpoint = CheckpointCoreAsync(_checkpoint, save, cancellationToken);
    }

    /// <summary>Reopens checkpoints after the host rejected shutdown and kept the window active.</summary>
    public void ResumeAfterShutdownFailure() => IsStopping = false;

    private async Task CheckpointCoreAsync(Task previous, bool save, CancellationToken cancellationToken)
    {
        await previous;
        if (_initialization is not null) { await _initialization; }
        if (!save || !_initialized) { return; }
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowPlacementState? state = _view.Capture();
            if (state is null) { return; }
            state.Validate();
            await _store.SaveAsync(state, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            await ReportAsync("window-placement-save", exception);
        }
    }

    private async Task ReportAsync(string operation, Exception exception)
    {
        try { await _errors.ReportAsync(new ApplicationError(operation, exception), CancellationToken.None); }
        catch (Exception reportingFailure) when (!ExceptionGraphClassifier.IsProcessFatal(reportingFailure))
        {
            Trace.TraceError("Window placement diagnostic could not be recorded ({0}).", reportingFailure.GetType().Name);
        }
    }
}
