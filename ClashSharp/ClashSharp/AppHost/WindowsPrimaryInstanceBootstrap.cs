using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace ClashSharp.Hosting;

/// <summary>Adapts Windows App SDK instance registration and activation redirection.</summary>
internal sealed class WindowsPrimaryInstanceBootstrap : IPrimaryInstanceBootstrap, IDisposable
{
    private const string PrimaryInstanceKey = "ClashSharp.Primary";
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly Action _activationRedirected;
    private AppInstance? _primaryInstance;
    private int _arbitrationStarted;
    private bool _disposed;

    public WindowsPrimaryInstanceBootstrap(DispatcherQueue dispatcherQueue, Action activationRedirected)
    {
        _dispatcherQueue = dispatcherQueue ?? throw new ArgumentNullException(nameof(dispatcherQueue));
        _activationRedirected = activationRedirected ?? throw new ArgumentNullException(nameof(activationRedirected));
    }

    public async Task<PrimaryInstanceOwnership> AcquireAsync(
        AppLaunchRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Interlocked.Exchange(ref _arbitrationStarted, 1) != 0)
        {
            throw new InvalidOperationException("Primary-instance arbitration can only run once per process.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        AppInstance registeredInstance = AppInstance.FindOrRegisterForKey(PrimaryInstanceKey);
        if (registeredInstance.IsCurrent)
        {
            _primaryInstance = registeredInstance;
            _primaryInstance.Activated += OnActivated;
            return PrimaryInstanceOwnership.Primary;
        }

        AppActivationArguments activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs()
            ?? throw new InvalidOperationException("Windows did not provide activation arguments for redirection.");
        await registeredInstance.RedirectActivationToAsync(activationArguments);
        cancellationToken.ThrowIfCancellationRequested();
        return PrimaryInstanceOwnership.Redirected;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_primaryInstance is not null)
        {
            _primaryInstance.Activated -= OnActivated;
            _primaryInstance.UnregisterKey();
            _primaryInstance = null;
        }
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        _dispatcherQueue.TryEnqueue(() => _activationRedirected());
    }
}
