using System;
using System.Diagnostics.CodeAnalysis;

namespace ClashSharp.Hosting.Startup;

/// <summary>Tracks native capabilities that may be absent while the standard WinUI window remains usable.</summary>
/// <remarks>
/// The native window handle enables handle-only decoration such as the custom title bar. Window-message
/// features, including the tray callback, additionally require a successfully installed window procedure.
/// </remarks>
internal sealed class StartupShellNativeCapabilityState
{
    /// <summary>Gets the acquired native window handle, or zero when unavailable.</summary>
    internal nint WindowHandle { get; private set; }

    /// <summary>Gets the previous window procedure, or zero when subclassing is unavailable.</summary>
    internal nint PreviousWindowProcedure { get; private set; }

    /// <summary>Gets whether handle-only native features may be attempted.</summary>
    internal bool HasWindowHandle => WindowHandle != 0;

    /// <summary>Gets whether features that receive owner-window messages may be attempted.</summary>
    internal bool HasWindowMessageHook =>
        HasWindowHandle && PreviousWindowProcedure != 0;

    /// <summary>Attempts to acquire the native handle without making it a startup requirement.</summary>
    internal bool TryAcquireWindowHandle(Func<nint> acquire)
    {
        ArgumentNullException.ThrowIfNull(acquire);

        if (HasWindowHandle)
        {
            return true;
        }

        if (!StartupShellSetupPolicy.TryRun(acquire, out nint handle)
            || handle == 0)
        {
            return false;
        }

        WindowHandle = handle;
        return true;
    }

    /// <summary>Attempts to install the window-message hook after a handle has been acquired.</summary>
    internal bool TryInstallWindowMessageHook(Func<nint, nint> install)
    {
        ArgumentNullException.ThrowIfNull(install);

        if (HasWindowMessageHook)
        {
            return true;
        }

        if (!HasWindowHandle
            || !StartupShellSetupPolicy.TryRun(
                () => install(WindowHandle),
                out nint previousWindowProcedure)
            || previousWindowProcedure == 0)
        {
            return false;
        }

        PreviousWindowProcedure = previousWindowProcedure;
        return true;
    }

    /// <summary>Attempts a feature that only requires the native window handle.</summary>
    internal bool TryRunWindowHandleFeature(Action<nint> setup)
    {
        ArgumentNullException.ThrowIfNull(setup);

        return HasWindowHandle
            && StartupShellSetupPolicy.TryRun(() => setup(WindowHandle));
    }

    /// <summary>Attempts to create a feature that requires window-message callbacks.</summary>
    internal bool TryCreateWindowMessageFeature<T>(
        Func<nint, T> create,
        [NotNullWhen(true)] out T? feature)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(create);

        if (!HasWindowMessageHook)
        {
            feature = null;
            return false;
        }

        if (!StartupShellSetupPolicy.TryRun(
                () => create(WindowHandle),
                out T? created)
            || created is null)
        {
            feature = null;
            return false;
        }

        feature = created;
        return true;
    }

    /// <summary>Tries to restore the previous window procedure before clearing native capability state.</summary>
    /// <returns>
    /// <see langword="true"/> when no hook remains; <see langword="false"/> when the delegate must stay rooted.
    /// </returns>
    internal bool TryReleaseWindowMessageHook(Func<nint, nint, bool> restore)
    {
        ArgumentNullException.ThrowIfNull(restore);

        nint handle = WindowHandle;
        nint previousWindowProcedure = PreviousWindowProcedure;
        if (handle == 0 || previousWindowProcedure == 0)
        {
            PreviousWindowProcedure = 0;
            WindowHandle = 0;
            return true;
        }

        if (!StartupShellSetupPolicy.TryRun(
                () => restore(handle, previousWindowProcedure),
                out bool restored)
            || !restored)
        {
            return false;
        }

        PreviousWindowProcedure = 0;
        WindowHandle = 0;
        return true;
    }
}
