using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Settings;
using Microsoft.UI.Xaml;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.UI.Shell;

namespace ClashSharp.Service;

/// <summary>Completes explicitly requested shell pinning only in the foreground application.</summary>
internal sealed class InstallationPinningService
{
    private readonly InstallationPinningCoordinator _coordinator = new();

    internal Task<InstallationPinningStatus> CompleteAsync(Window owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        if (window == 0 || GetForegroundWindow() != window) { return Task.FromResult(InstallationPinningStatus.Deferred); }
        return _coordinator.CompleteAsync(new WindowsPinningOperations(window, ApplicationData.Current.LocalSettings.Values), cancellationToken);
    }

    private sealed class WindowsPinningOperations(nint window, System.Collections.Generic.IDictionary<string, object> values) : IInstallationPinningOperations
    {
        private TaskbarManager? _manager;

        public bool IsForeground => GetForegroundWindow() == window;

        public string? ReadPendingRequest() => InstallationTaskbarRequestStore.ReadPending(values);

        public bool TryConsumeRequest(string requestId) => InstallationTaskbarRequestStore.TryConsume(values, requestId);

        public async Task<InstallationPinningStatus> GetStatusAsync(CancellationToken cancellationToken)
        {
            try
            {
                if (!ApiInformation.IsMethodPresent("Windows.UI.Shell.TaskbarManager", "RequestPinCurrentAppAsync")) { return InstallationPinningStatus.Unavailable; }
                _manager = TaskbarManager.GetDefault();
                bool pinned = await _manager.IsCurrentAppPinnedAsync().AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                return pinned ? InstallationPinningStatus.AlreadyPinned
                    : _manager.IsPinningAllowed ? InstallationPinningStatus.Ready : InstallationPinningStatus.Unavailable;
            }
            catch (Exception exception) when (IsUnavailable(exception)) { return InstallationPinningStatus.Unavailable; }
        }

        public async Task<InstallationPinningStatus> RequestPinAsync(CancellationToken cancellationToken)
        {
            try
            {
                // Submit before applying cancellation to the await: the owned request is already
                // consumed and must not be lost without an attempted native confirmation.
                bool pinned = await (_manager ?? throw new InvalidOperationException("Taskbar status was not observed."))
                    .RequestPinCurrentAppAsync().AsTask(cancellationToken);
                return pinned ? InstallationPinningStatus.Pinned : InstallationPinningStatus.NotPinned;
            }
            catch (Exception exception) when (IsUnavailable(exception)) { return InstallationPinningStatus.Unavailable; }
        }

        private static bool IsUnavailable(Exception exception) => !ExceptionGraphClassifier.IsProcessFatal(exception)
            && exception is COMException or UnauthorizedAccessException or PlatformNotSupportedException;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();
}
