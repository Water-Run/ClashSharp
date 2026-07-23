using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ClashSharp;

/// <summary>Application root class responsible for lifecycle management and global window access.</summary>
/// <remarks>
/// Invariants: A secondary process never constructs a host or window; a primary window is published only while it is alive.
/// Thread safety: All access occurs on the UI thread.
/// Side effects: Arbitrates process ownership, starts the primary host, and owns awaited host disposal.
/// </remarks>
public partial class App : Microsoft.UI.Xaml.Application
{
    /// <summary>Backing field for the singleton main window reference.</summary>
    private static Window? _mainWindow;

    private readonly ProcessLifetimeRunner _lifetimeRunner = new();
    private readonly ApplicationLifetimeRequestChannel _lifetimeRequests = new();
    private WindowsPrimaryInstanceBootstrap? _primaryInstanceBootstrap;
    private Task? _shutdownTask;
    private Task? _lifetimeRequestConsumer;
    private bool _activationPending;

    /// <summary>Gets the primary application window instance for global access.</summary>
    /// <value>The live primary <see cref="Window"/>; null before attachment, in a secondary process, and after close.</value>
    public static Window? MainWindow => _mainWindow;

    /// <summary>Initializes the singleton application object and its XAML resources.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>Creates the main window and activates it when the application is launched.</summary>
    /// <param name="args">Launch activation details provided by the platform. Not null.</param>
    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            DispatcherQueue dispatcherQueue = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("The WinUI dispatcher is unavailable during launch.");
            _primaryInstanceBootstrap = new WindowsPrimaryInstanceBootstrap(dispatcherQueue, BringPrimaryWindowToFront);
            ApplicationBootstrapper bootstrapper = new(
                _primaryInstanceBootstrap,
                () => ClashSharpAppHostFactory.Build(AttachMainWindow, _lifetimeRequests),
                _lifetimeRunner);
            ApplicationLaunchResult result = await bootstrapper.LaunchAsync(
                new AppLaunchRequest(args.Arguments),
                CancellationToken.None);
            if (result.Disposition != ApplicationLaunchDisposition.Running)
            {
                await StopAndExitAsync(restart: false);
                return;
            }

            _lifetimeRequestConsumer = ConsumeLifetimeRequestAsync(dispatcherQueue);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"ClashSharp startup failed: {exception}");
            await StopAndExitAsync(restart: false);
        }
    }

    private void AttachMainWindow(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (_mainWindow is not null)
        {
            throw new InvalidOperationException("The primary window is already attached.");
        }

        _mainWindow = window;
        _mainWindow.Closed += OnMainWindowClosed;
        if (_activationPending)
        {
            _activationPending = false;
            _mainWindow.Activate();
        }
    }

    private void BringPrimaryWindowToFront()
    {
        if (_mainWindow is null)
        {
            _activationPending = true;
            return;
        }

        _mainWindow.Activate();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }

        _lifetimeRequests.TryRequest(ApplicationLifetimeRequest.Exit("main-window"));
    }

    private Task StopAndExitAsync(
        bool restart,
        ApplicationLifetimeRequest? lifetimeRequest = null)
    {
        return _shutdownTask ??= StopAndExitCoreAsync(restart, lifetimeRequest);
    }

    private async Task ConsumeLifetimeRequestAsync(DispatcherQueue dispatcherQueue)
    {
        ApplicationLifetimeRequest request = await _lifetimeRequests
            .ReadAsync(CancellationToken.None)
            .ConfigureAwait(false);
        TaskCompletionSource<object?> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcherQueue.TryEnqueue(() => ProcessLifetimeRequestOnDispatcher(request, completion)))
        {
            completion.TrySetException(new InvalidOperationException(
                "The UI dispatcher rejected the application lifetime request."));
        }

        try
        {
            await completion.Task.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"ClashSharp lifetime request failed: {exception}");
        }
    }

    private async void ProcessLifetimeRequestOnDispatcher(
        ApplicationLifetimeRequest request,
        TaskCompletionSource<object?> completion)
    {
        try
        {
            await StopAndExitAsync(
                request.Kind == ApplicationLifetimeRequestKind.Restart,
                request);
            completion.TrySetResult(null);
        }
        catch (Exception exception)
        {
            completion.TrySetException(exception);
        }
    }

    private async Task StopAndExitCoreAsync(
        bool restart,
        ApplicationLifetimeRequest? lifetimeRequest)
    {
        string? executablePath = restart ? ResolveExecutablePath() : null;
        try
        {
            if (lifetimeRequest is null)
            {
                await _lifetimeRunner.StopAsync(CancellationToken.None);
            }
            else
            {
                await _lifetimeRunner.ProcessAsync(lifetimeRequest, CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"ClashSharp shutdown failed: {exception}");
            return;
        }

        try
        {
            _primaryInstanceBootstrap?.Dispose();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"ClashSharp instance cleanup failed: {exception}");
        }
        finally
        {
            _primaryInstanceBootstrap = null;
        }

        if (executablePath is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = executablePath,
                    UseShellExecute = true,
                });
            }
            catch (Exception exception)
            {
                Debug.WriteLine($"ClashSharp restart failed: {exception}");
            }
        }

        Exit();
    }

    private static string ResolveExecutablePath()
    {
        return Environment.ProcessPath
            ?? Process.GetCurrentProcess().MainModule?.FileName
            ?? "ClashSharp.exe";
    }
}
