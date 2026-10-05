using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using ClashSharp.ApplicationModel.Presentation;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace ClashSharp.Presentation.Adapters;

/// <summary>
/// Translates stable AppWindow changes and native monitor geometry on the window's UI thread.
/// Monitor handles are borrowed only inside this adapter. Normal bounds never come from minimized
/// coordinates or the full maximized rectangle, avoiding taskbar-coordinate drift.
/// </summary>
internal sealed class WinUiWindowPlacementView : IWindowPlacementView
{
    private readonly AppWindow _window;
    private readonly nint _handle;
    private readonly WindowPlacementTracker _tracker = new();
    private bool _restoring;
    private WindowPlacementState? _deferredRestore;

    internal WinUiWindowPlacementView(AppWindow window, nint handle)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        if (handle == 0) { throw new ArgumentException("A live window handle is required.", nameof(handle)); }
        _handle = handle;
    }

    public void Observe()
    {
        if (_restoring || !_window.IsVisible || _window.Presenter is not OverlappedPresenter presenter
            || presenter.State == OverlappedPresenterState.Minimized) { return; }
        if (_deferredRestore is { } deferred)
        {
            _deferredRestore = null;
            Restore(deferred);
            return;
        }
        WindowPlacementMonitor monitor = ReadMonitor(MonitorFromWindow(_handle, 2));
        PointInt32 position = _window.Position;
        SizeInt32 size = _window.Size;
        _tracker.Observe(monitor, new(position.X, position.Y, size.Width, size.Height), ReadDpi(),
            visible: true, minimized: false, maximized: presenter.State == OverlappedPresenterState.Maximized);
    }

    public WindowPlacementState? Capture()
    {
        Observe();
        return _tracker.Current;
    }

    public void Restore(WindowPlacementState state)
    {
        state.Validate();
        if (_window.Presenter is not OverlappedPresenter presenter) { return; }
        if (!_window.IsVisible || presenter.State == OverlappedPresenterState.Minimized)
        {
            _tracker.Seed(state);
            _deferredRestore = state;
            return;
        }
        WindowPlacementMonitor target = WindowPlacementPolicy.SelectMonitor(state, ReadMonitors());
        _restoring = true;
        try
        {
            if (presenter.State != OverlappedPresenterState.Restored) { presenter.Restore(); }
            // Move onto the chosen monitor before asking for this window's effective DPI. This
            // avoids using a process-wide DPI value for a different monitor under per-monitor V2.
            WindowPlacementBounds initial = WindowPlacementPolicy.Resolve(state, target, ReadDpi());
            Move(initial);
            // A display can disappear while moving. Resolve again against the monitor Windows
            // actually selected, so the final resize cannot restore an obsolete off-screen area.
            target = ReadMonitor(MonitorFromWindow(_handle, 2));
            uint dpi = ReadDpi();
            WindowPlacementBounds normal = WindowPlacementPolicy.Resolve(state, target, dpi);
            if (normal != initial) { Move(normal); }
            _tracker.Seed(new(WindowPlacementState.CurrentSchema, target.Name, normal, target.WorkArea, dpi, state.IsMaximized));
            if (state.IsMaximized) { presenter.Maximize(); }
        }
        finally { _restoring = false; }
        Observe();
    }

    public (int Width, int Height) ConstrainMinimumSize(int width, int height)
    {
        try
        {
            WindowPlacementBounds area = ReadMonitor(MonitorFromWindow(_handle, 2)).WorkArea;
            return (Math.Min(width, area.Width), Math.Min(height, area.Height));
        }
        catch (Win32Exception)
        {
            // Display teardown can temporarily make monitor geometry unavailable in WM_GETMINMAXINFO.
            // Retain the existing logical minimum instead of throwing through the native callback.
            return (width, height);
        }
    }

    private void Move(WindowPlacementBounds bounds) => _window.MoveAndResize(new RectInt32(bounds.X, bounds.Y, bounds.Width, bounds.Height));
    private uint ReadDpi()
    {
        uint dpi = GetDpiForWindow(_handle);
        if (dpi is < 48 or > 768) { throw new InvalidOperationException("Window DPI is unavailable."); }
        return dpi;
    }
    private static WindowPlacementMonitor ReadMonitor(nint handle)
    {
        var information = new MonitorInformation { Size = Marshal.SizeOf<MonitorInformation>(), Device = string.Empty };
        if (handle == 0 || !GetMonitorInfo(handle, ref information)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        NativeRectangle work = information.Work;
        return new(information.Device, new(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top));
    }
    private static IReadOnlyList<WindowPlacementMonitor> ReadMonitors()
    {
        var monitors = new List<WindowPlacementMonitor>();
        Exception? failure = null;
        MonitorCallback callback = (nint monitor, nint _, ref NativeRectangle _, nint _) =>
        {
            try { monitors.Add(ReadMonitor(monitor)); return true; }
            catch (Exception exception) { failure = exception; return false; }
        };
        bool enumerated = EnumDisplayMonitors(0, 0, callback, 0);
        GC.KeepAlive(callback);
        if (failure is not null) { throw new InvalidOperationException("Monitor enumeration failed.", failure); }
        if (!enumerated) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return monitors;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRectangle { public int Left; public int Top; public int Right; public int Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInformation
    {
        public int Size;
        public NativeRectangle Monitor;
        public NativeRectangle Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MonitorCallback(nint monitor, nint device, ref NativeRectangle rectangle, nint data);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(nint monitor, ref MonitorInformation information);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint device, nint clip, MonitorCallback callback, nint data);
    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetDpiForWindow(nint window);
}
