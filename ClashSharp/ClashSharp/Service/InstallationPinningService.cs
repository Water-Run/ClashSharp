using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Settings;
using Microsoft.UI.Xaml;
using Windows.Foundation.Metadata;
using Windows.Storage;
using Windows.UI.Shell;

namespace ClashSharp.Service;

/// <summary>Completes explicitly requested shell pinning only in the foreground application.</summary>
internal sealed class InstallationPinningService
{
    internal async Task CompleteAsync(Window owner, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(owner);
        if (window == 0 || GetForegroundWindow() != window) { return; }
        var values = ApplicationData.Current.LocalSettings.Values;
        string? taskbar = Pending(values, InstallationSetupProtocol.TaskbarRequestKey, InstallationSetupProtocol.TaskbarReceiptKey);
        cancellationToken.ThrowIfCancellationRequested();
        if (taskbar is null || GetForegroundWindow() != window) { return; }
        try
        {
            if (ApiInformation.IsMethodPresent("Windows.UI.Shell.TaskbarManager", "RequestPinCurrentAppAsync"))
            {
                TaskbarManager manager = TaskbarManager.GetDefault();
                bool allowed = manager.IsPinningAllowed;
                bool pinned = allowed && await manager.IsCurrentAppPinnedAsync().AsTask(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (GetForegroundWindow() != window) { return; }
                Consume(values, InstallationSetupProtocol.TaskbarRequestKey, InstallationSetupProtocol.TaskbarReceiptKey, taskbar);
                if (allowed && !pinned)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (GetForegroundWindow() == window) { _ = await manager.RequestPinCurrentAppAsync().AsTask(cancellationToken); }
                }
            }
            else { Consume(values, InstallationSetupProtocol.TaskbarRequestKey, InstallationSetupProtocol.TaskbarReceiptKey, taskbar); }
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)
            && exception is COMException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            Consume(values, InstallationSetupProtocol.TaskbarRequestKey, InstallationSetupProtocol.TaskbarReceiptKey, taskbar);
            // LAF, policy or an unsupported desktop implementation cannot turn installation into
            // an automatic retry loop. The user's existing pins and Windows policy remain intact.
        }
    }

    private static string? Pending(System.Collections.Generic.IDictionary<string, object> values, string request, string receipt)
    {
        if (!values.TryGetValue(request, out object? value) || value is not string text
            || !Guid.TryParseExact(text, "N", out Guid id) || id == Guid.Empty || id.ToString("N") != text) { return null; }
        return values.TryGetValue(receipt, out object? handled) && Equals(handled, text) ? null : text;
    }

    private static void Consume(System.Collections.Generic.IDictionary<string, object> values, string request, string receipt, string id)
    {
        if (!values.TryGetValue(request, out object? current) || !Equals(current, id)) { return; }
        values[receipt] = id;
        values.Remove(request);
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint GetForegroundWindow();
}
