using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Service;
using Microsoft.UI.Dispatching;
using Microsoft.Windows.AppLifecycle;

namespace ClashSharp.Hosting;

/// <summary>Adapts Windows App SDK instance registration and activation redirection.</summary>
internal sealed class WindowsPrimaryInstanceBootstrap : IPrimaryInstanceBootstrap, IDisposable
{
    private const string PrimaryInstanceKey = "ClashSharp.Primary";
    private const string RestoreInstanceKey = "ClashSharp.RestoreFallback";
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

        AppInstance? registeredInstance = null;
        bool isRestoreFallback = request.Arguments.Contains(
            StartupRestoreFallbackService.HelperArgument,
            StringComparison.OrdinalIgnoreCase);
        return await StartupInstanceArbitration.AcquireAsync(
            isRestoreFallback,
            TryRegister,
            FindOtherProcess,
            WaitForExitAsync,
            RedirectAsync,
            cancellationToken);

        bool TryRegister(bool restore)
        {
            registeredInstance = AppInstance.FindOrRegisterForKey(restore ? RestoreInstanceKey : PrimaryInstanceKey);
            if (!registeredInstance.IsCurrent)
            {
                return false;
            }

            _primaryInstance = registeredInstance;
            if (!restore)
            {
                _primaryInstance.Activated += OnActivated;
            }

            return true;
        }

        async Task RedirectAsync(CancellationToken token)
        {
            AppActivationArguments activationArguments = AppInstance.GetCurrent().GetActivatedEventArgs()
                ?? throw new InvalidOperationException("Windows did not provide activation arguments for redirection.");
            await registeredInstance!.RedirectActivationToAsync(activationArguments);
            token.ThrowIfCancellationRequested();
        }
    }

    private static int? FindOtherProcess(bool restore)
    {
        string key = restore ? RestoreInstanceKey : PrimaryInstanceKey;
        AppInstance? instance = AppInstance.GetInstances().FirstOrDefault(
            candidate => !candidate.IsCurrent && string.Equals(candidate.Key, key, StringComparison.Ordinal));
        return instance is null ? null : checked((int)instance.ProcessId);
    }

    private static async Task WaitForExitAsync(int processId, CancellationToken cancellationToken)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            // The helper can finish between discovery and opening its process handle.
            return;
        }

        using (process)
        {
            await process.WaitForExitAsync(cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
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
