using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting;
using ClashSharp.Hosting.Startup;
using ClashSharp.Service;
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
    private const int DurableHeadlessShutdownMaximumAttempts = 1;
    private const int DispatcherUnavailableShutdownMaximumAttempts = 5;
    private const int HeadlessShutdownMaximumAttempts = 2;
    private const int LifetimeRequestDispatcherMaximumAttempts = 5;
    private static readonly TimeSpan LifetimeRequestDispatcherRetryDelay =
        TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan StartupShellFirstFrameTimeout =
        TimeSpan.FromSeconds(5);

    /// <summary>Backing field for the singleton main window reference.</summary>
    private static Window? _mainWindow;

    private readonly ProcessLifetimeRunner _lifetimeRunner = new();
    private readonly ApplicationLifetimeRequestChannel _lifetimeRequests = new();
    private readonly StartupCompletionGate<MainWindowStartupContext> _startupCompletion = new();
    private readonly PersistentStartupDiagnosticSink _startupDiagnostics = new();
    private readonly HeadlessShutdownPolicy _durableHeadlessShutdownPolicy =
        new(DurableHeadlessShutdownMaximumAttempts);
    private readonly HeadlessShutdownPolicy _headlessShutdownPolicy =
        new(HeadlessShutdownMaximumAttempts);
    private readonly HeadlessShutdownPolicy _dispatcherUnavailableShutdownPolicy =
        new(DispatcherUnavailableShutdownMaximumAttempts);
    private readonly IInstallerTransactionStateReader _installerTransactionStateReader =
        new InstallerTransactionStateReader();
    private readonly object _shutdownSyncLock = new();
    private WindowsPrimaryInstanceBootstrap? _primaryInstanceBootstrap;
    private RecoveryWatchdogCoordinator? _recoveryWatchdog;
    private Task<bool>? _shutdownTask;
    private Task? _lifetimeRequestConsumer;
    private long _shutdownAttemptVersion;
    private bool _activationPending;
    private bool _primaryOwnershipConfirmed;
    private bool _startupShellSuppressed;
    private InstallerTransactionState _installerTransactionState =
        InstallerTransactionState.Invalid;

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
            AppLaunchRequest launchRequest = new(args.Arguments);
            ApplicationBootstrapper bootstrapper = new(
                _primaryInstanceBootstrap,
                () => ClashSharpAppHostFactory.Build(launchRequest,
                    CompleteMainWindowStartup,
                    _lifetimeRequests,
                    _startupDiagnostics,
                    _installerTransactionState),
                _lifetimeRunner,
                CreatePrimaryStartupShell);
            ApplicationLaunchResult result = await bootstrapper.LaunchAsync(
                launchRequest,
                CancellationToken.None);
            if (result.Disposition is ApplicationLaunchDisposition.Redirected
                or ApplicationLaunchDisposition.ExitRequested)
            {
                await StopAndExitHeadlessAsync(restart: false);
                return;
            }

            if (result.Disposition == ApplicationLaunchDisposition.Fatal && _startupShellSuppressed)
            {
                await StopAndExitHeadlessAsync(restart: false);
                return;
            }

            if (result.Disposition == ApplicationLaunchDisposition.Fatal)
            {
                ShowStartupFailure(result.StartupResult?.DiagnosticCode ?? "startup-fatal");
            }

            await AwaitLifetimeRequestConsumerAsync(dispatcherQueue);
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
            _startupDiagnostics.RecordUnhandled(exception);
            DispatcherQueue? dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            if (_lifetimeRequests.HasAcceptedRequest && dispatcherQueue is not null)
            {
                await AwaitLifetimeRequestConsumerAsync(dispatcherQueue);
                return;
            }

            if (!_primaryOwnershipConfirmed || _startupShellSuppressed)
            {
                await StopAndExitHeadlessAsync(restart: false);
                return;
            }

            if (_mainWindow is null)
            {
                MainWindow window = new(_lifetimeRequests);
                AttachMainWindow(window);
                window.Activate();
            }

            ShowStartupFailure("startup-unhandled-exception");
            if (dispatcherQueue is not null)
            {
                await AwaitLifetimeRequestConsumerAsync(dispatcherQueue);
            }
        }
    }

    private async Task CreatePrimaryStartupShell(AppLaunchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _primaryOwnershipConfirmed = true;
        _startupDiagnostics.Start();
        _startupShellSuppressed = request.Arguments.Contains(
            StartupRestoreFallbackService.HelperArgument,
            StringComparison.OrdinalIgnoreCase);
        _recoveryWatchdog = await RecoveryWatchdogCoordinator
            .AcquireAsync(cancellationToken)
            .ConfigureAwait(true);
        _installerTransactionState = _installerTransactionStateReader.Read();
        if (_startupShellSuppressed)
        {
            return;
        }

        if (_installerTransactionState == InstallerTransactionState.Clear
            && !_recoveryWatchdog.TryArm())
        {
            Debug.WriteLine(
                "ClashSharp recovery watchdog was unavailable; next-start proxy recovery remains active.");
        }

        MainWindow window = new(_lifetimeRequests);
        AttachMainWindow(window);
        FirstFrameRenderingGate firstFrame = new();
        Microsoft.UI.Xaml.Media.CompositionTarget.Rendered += OnRendered;
        try
        {
            window.Activate();
            await firstFrame.WaitAsync(
                StartupShellFirstFrameTimeout,
                cancellationToken);
        }
        finally
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendered -= OnRendered;
        }

        cancellationToken.ThrowIfCancellationRequested();

        void OnRendered(object? sender, object args)
        {
            firstFrame.SignalRendering();
        }
    }

    private void CompleteMainWindowStartup(MainWindowStartupContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (_mainWindow is not MainWindow window)
        {
            throw new InvalidOperationException("The primary startup shell is unavailable.");
        }

        if (!_startupCompletion.TryAccept(
            context,
            _lifetimeRequests.HasAcceptedRequest,
            out MainWindowStartupContext? completion))
        {
            return;
        }

        CompleteMainWindowStartup(window, completion);
    }

    private static void CompleteMainWindowStartup(
        MainWindow window,
        MainWindowStartupContext context)
    {
        window.CompleteStartup(
            context.TriggerEvents,
            context.Actions,
            context.Lifecycle,
            context.TrayCommands,
            context.StartupConflicts);
    }

    private void ShowStartupFailure(string diagnostic)
    {
        if (_mainWindow is MainWindow window)
        {
            window.ShowStartupFailure(diagnostic);
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
            BringPrimaryWindowToFront();
        }
    }

    private void BringPrimaryWindowToFront()
    {
        if (_mainWindow is not IPrimaryWindowActivationTarget window)
        {
            _activationPending = true;
            return;
        }

        PrimaryWindowActivation.BringToFront(window);
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

    private async Task AwaitLifetimeRequestConsumerAsync(DispatcherQueue dispatcherQueue)
    {
        Task consumer = _lifetimeRequestConsumer ??=
            ConsumeLifetimeRequestAsync(dispatcherQueue);
        try
        {
            await consumer.ConfigureAwait(false);
        }
        finally
        {
            if (ReferenceEquals(_lifetimeRequestConsumer, consumer))
            {
                _lifetimeRequestConsumer = null;
            }
        }
    }

    private Task<bool> StopAndExitAsync(
        bool restart,
        ApplicationLifetimeRequest? lifetimeRequest = null)
    {
        lock (_shutdownSyncLock)
        {
            if (_shutdownTask is null)
            {
                long attemptVersion = ++_shutdownAttemptVersion;
                _shutdownTask = StopAndExitAttemptAsync(
                    attemptVersion,
                    restart,
                    lifetimeRequest);
            }

            return _shutdownTask;
        }
    }

    private async Task StopAndExitHeadlessAsync(
        bool restart,
        ApplicationLifetimeRequest? lifetimeRequest = null)
    {
        HeadlessShutdownPolicy policy = lifetimeRequest?.Handoff is null
            ? _headlessShutdownPolicy
            : _durableHeadlessShutdownPolicy;
        bool completed = await policy.TryCompleteAsync(
            () => StopAndExitAsync(restart, lifetimeRequest),
            exception =>
            {
                Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
                TryLogShutdownFailure(exception, lifetimeRequest);
            });
        if (completed)
        {
            return;
        }

        await ForceExitAfterShutdownFailureAsync(lifetimeRequest);
    }

    private async Task ConsumeLifetimeRequestAsync(DispatcherQueue dispatcherQueue)
    {
        while (true)
        {
            ApplicationLifetimeRequest request = await _lifetimeRequests
                .ReadAsync(CancellationToken.None)
                .ConfigureAwait(false);
            TaskCompletionSource<bool> completion =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            bool dispatchAccepted = await DispatcherOperationPolicy.RunWithRetryOrFallbackAsync(
                callback => dispatcherQueue.TryEnqueue(() => callback()),
                () => ProcessLifetimeRequestOnDispatcher(request, completion),
                LifetimeRequestDispatcherMaximumAttempts,
                LifetimeRequestDispatcherRetryDelay,
                () => CompleteLifetimeRequestWithoutDispatcherAsync(request),
                CancellationToken.None).ConfigureAwait(false);
            if (!dispatchAccepted)
            {
                return;
            }

            bool succeeded;
            try
            {
                succeeded = await completion.Task.ConfigureAwait(false);
            }
            catch (Exception exception) when (
                StartupCompletionFailurePolicy.IsRecoverable(exception))
            {
                Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
                succeeded = false;
            }

            if (succeeded)
            {
                return;
            }

            bool retryScheduled = await _lifetimeRequests
                .RetryFailedRequestAsync(request, CancellationToken.None)
                .ConfigureAwait(false);
            if (!retryScheduled)
            {
                Debug.WriteLine(
                    $"ClashSharp released failed lifetime request '{request.Source}' without automatic retry.");
                if (_lifetimeRunner.CanResumeAttachedHost)
                {
                    TryResumePendingMainWindowStartup(dispatcherQueue);
                }
                else
                {
                    await ForceExitStoppedHostOnDispatcherAsync(dispatcherQueue, request)
                        .ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    private async Task CompleteLifetimeRequestWithoutDispatcherAsync(
        ApplicationLifetimeRequest request)
    {
        string? executablePath = request.Kind == ApplicationLifetimeRequestKind.Restart
            ? ResolveExecutablePath()
            : null;
        TryLogDispatcherUnavailableShutdown(request.Source);
        bool hostReleased = await _dispatcherUnavailableShutdownPolicy.TryCompleteAsync(
            () => StopHostWithoutDispatcherAsync(request),
            exception =>
            {
                Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
                TryLogShutdownFailure(exception, request);
            }).ConfigureAwait(false);
        if (!hostReleased)
        {
            TryLogForcedShutdown(request.Source);
        }
        else
        {
            CompleteRecoveryWatchdogNormalExit();
        }

        _startupCompletion.Abandon();
        await CompleteStartupDiagnosticsAsync().ConfigureAwait(false);
        ReleasePrimaryInstanceOwnership();
        if (hostReleased && executablePath is not null)
        {
            TryStartRestartProcess(executablePath);
        }
    }

    private async Task<bool> StopHostWithoutDispatcherAsync(
        ApplicationLifetimeRequest request)
    {
        try
        {
            if (request.TerminalStatePersistence
                == ApplicationLifetimeTerminalStatePersistence.Confirmed)
            {
                await _lifetimeRunner.StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            else
            {
                await _lifetimeRunner
                    .ProcessAsync(request, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
        catch (ApplicationHostDisposalException exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            TryLogTerminalHostDisposalFailure(exception, request);
        }

        return !_lifetimeRunner.HasAttachedHost;
    }

    private Task ForceExitStoppedHostOnDispatcherAsync(
        DispatcherQueue dispatcherQueue,
        ApplicationLifetimeRequest request)
    {
        return DispatcherOperationPolicy.RunOrFallbackAsync(
            callback => dispatcherQueue.TryEnqueue(() => callback()),
            () => ForceExitAfterShutdownFailureAsync(request),
            () => ReleaseStoppedHostWithoutDispatcherAsync(request));
    }

    private async Task ReleaseStoppedHostWithoutDispatcherAsync(
        ApplicationLifetimeRequest request)
    {
        // A rejected dispatcher is already shutting down. Avoid all Window/Application calls here;
        // release only thread-safe process ownership and let the WinUI message loop finish naturally.
        TryLogDispatcherUnavailableShutdown(request.Source);
        _startupCompletion.Abandon();
        await CompleteStartupDiagnosticsAsync().ConfigureAwait(false);
        ReleasePrimaryInstanceOwnership();
    }

    private void TryResumePendingMainWindowStartup(DispatcherQueue dispatcherQueue)
    {
        if (!DispatcherOperationPolicy.TryEnqueue(
            callback => dispatcherQueue.TryEnqueue(() => callback()),
            ResumePendingMainWindowStartup))
        {
            Debug.WriteLine(
                "ClashSharp UI dispatcher rejected deferred startup-shell completion.");
        }
    }

    private void ResumePendingMainWindowStartup()
    {
        bool isHostAttached = _lifetimeRunner.CanResumeAttachedHost
            && _mainWindow is MainWindow;
        if (!_startupCompletion.TryResume(
            _lifetimeRequests.HasAcceptedRequest,
            isHostAttached,
            out MainWindowStartupContext? completion)
            || _mainWindow is not MainWindow window)
        {
            return;
        }

        try
        {
            CompleteMainWindowStartup(window, completion);
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
            _startupDiagnostics.RecordUnhandled(exception);
            ShowStartupFailure("startup-deferred-completion-failed");
        }
    }

    private async void ProcessLifetimeRequestOnDispatcher(
        ApplicationLifetimeRequest request,
        TaskCompletionSource<bool> completion)
    {
        try
        {
            bool restart = request.Kind == ApplicationLifetimeRequestKind.Restart;
            if (_mainWindow is null)
            {
                await StopAndExitHeadlessAsync(restart, request);
                completion.TrySetResult(true);
            }
            else
            {
                bool succeeded = await StopAndExitAsync(restart, request);
                completion.TrySetResult(succeeded);
            }
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            completion.TrySetException(exception);
        }
    }

    private async Task<bool> StopAndExitAttemptAsync(
        long attemptVersion,
        bool restart,
        ApplicationLifetimeRequest? lifetimeRequest)
    {
        await Task.Yield();
        try
        {
            bool succeeded = await StopAndExitCoreAsync(restart, lifetimeRequest);
            if (!succeeded)
            {
                ReleaseShutdownAttempt(attemptVersion);
            }

            return succeeded;
        }
        catch
        {
            ReleaseShutdownAttempt(attemptVersion);
            throw;
        }
    }

    private void ReleaseShutdownAttempt(long attemptVersion)
    {
        lock (_shutdownSyncLock)
        {
            if (_shutdownAttemptVersion == attemptVersion)
            {
                _shutdownTask = null;
            }
        }
    }

    private async Task<bool> StopAndExitCoreAsync(
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
        catch (ApplicationHostDisposalException exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
            TryLogTerminalHostDisposalFailure(exception, lifetimeRequest);
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
            TryLogShutdownFailure(exception, lifetimeRequest);
            if (_mainWindow is MainWindow window)
            {
                window.NotifyExitRequestFailed();
            }

            return false;
        }

        _startupCompletion.Abandon();
        CompleteRecoveryWatchdogNormalExit();
        await CompleteStartupDiagnosticsAsync();
        ReleasePrimaryInstanceOwnership();

        if (executablePath is not null)
        {
            TryStartRestartProcess(executablePath);
        }

        ApproveWindowCloseForApplicationExit();
        Exit();
        return true;
    }

    private async Task ForceExitAfterShutdownFailureAsync(ApplicationLifetimeRequest? request)
    {
        string source = request?.Source ?? "application";
        Debug.WriteLine(
            $"ClashSharp exhausted bounded shutdown retries for '{source}'; forcing process exit.");
        TryLogForcedShutdown(source);
        _startupCompletion.Abandon();
        await CompleteStartupDiagnosticsAsync();
        ReleasePrimaryInstanceOwnership();
        ApproveWindowCloseForApplicationExit();
        Exit();
    }

    private async Task CompleteStartupDiagnosticsAsync()
    {
        try
        {
            await _startupDiagnostics.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            string outcome = exception is TimeoutException
                ? "flush timed out"
                : "could not be persisted";
            Debug.WriteLine(
                $"ClashSharp startup diagnostics {outcome} ({exception.GetType().FullName}).");
        }
    }

    private void ReleasePrimaryInstanceOwnership()
    {
        try
        {
            _primaryInstanceBootstrap?.Dispose();
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
        }
        finally
        {
            _primaryInstanceBootstrap = null;
        }
    }

    private void CompleteRecoveryWatchdogNormalExit()
    {
        RecoveryWatchdogCoordinator? watchdog = _recoveryWatchdog;
        if (watchdog is null)
        {
            return;
        }

        watchdog.Disarm();
        watchdog.Dispose();
        _recoveryWatchdog = null;
    }

    private static void ApproveWindowCloseForApplicationExit()
    {
        if (_mainWindow is MainWindow window)
        {
            window.ApproveApplicationExit();
        }
    }

    private void TryLogShutdownFailure(
        Exception exception,
        ApplicationLifetimeRequest? request)
    {
        try
        {
            string source = request?.Source ?? "application";
            _startupDiagnostics.RecordLifecycleFailure(
                $"Application shutdown requested by '{source}' failed; exit remains available for retry.",
                exception);
        }
        catch (Exception diagnosticException) when (
            StartupCompletionFailurePolicy.IsRecoverable(diagnosticException))
        {
            Debug.WriteLine(
                $"ClashSharp could not queue shutdown failure diagnostics ({diagnosticException.GetType().FullName}).");
        }
    }

    private void TryLogForcedShutdown(string source)
    {
        try
        {
            _startupDiagnostics.RecordLifecycleFailure(
                $"Application shutdown requested by '{source}' exhausted bounded retries; process exit was forced.",
                null);
        }
        catch (Exception diagnosticException) when (
            StartupCompletionFailurePolicy.IsRecoverable(diagnosticException))
        {
            Debug.WriteLine(
                $"ClashSharp could not queue forced shutdown diagnostics ({diagnosticException.GetType().FullName}).");
        }
    }

    private void TryLogDispatcherUnavailableShutdown(string source)
    {
        try
        {
            _startupDiagnostics.RecordLifecycleFailure(
                $"Application shutdown requested by '{source}' could not enqueue terminal UI work because dispatcher shutdown was already in progress; non-UI ownership cleanup continued.",
                null);
        }
        catch (Exception diagnosticException) when (
            StartupCompletionFailurePolicy.IsRecoverable(diagnosticException))
        {
            Debug.WriteLine(
                $"ClashSharp could not queue dispatcher-shutdown diagnostics ({diagnosticException.GetType().FullName}).");
        }
    }

    private void TryLogTerminalHostDisposalFailure(
        ApplicationHostDisposalException exception,
        ApplicationLifetimeRequest? request)
    {
        try
        {
            string source = request?.Source ?? "application";
            _startupDiagnostics.RecordLifecycleFailure(
                $"Application host disposal requested by '{source}' failed after ownership became terminal; process exit continued.",
                exception);
        }
        catch (Exception diagnosticException) when (
            StartupCompletionFailurePolicy.IsRecoverable(diagnosticException))
        {
            Debug.WriteLine(
                $"ClashSharp could not queue terminal host disposal diagnostics ({diagnosticException.GetType().FullName}).");
        }
    }

    private static string ResolveExecutablePath()
    {
        string? processPath = Environment.ProcessPath;
        if (!string.IsNullOrWhiteSpace(processPath))
        {
            return processPath;
        }

        using Process process = Process.GetCurrentProcess();
        return process.MainModule?.FileName ?? "ClashSharp.exe";
    }

    private static void TryStartRestartProcess(string executablePath)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executablePath,
                UseShellExecute = true,
            });
        }
        catch (Exception exception) when (
            StartupCompletionFailurePolicy.IsRecoverable(exception))
        {
            Debug.WriteLine(StartupExceptionDiagnostics.FormatDebugMessage(exception));
        }
    }
}
