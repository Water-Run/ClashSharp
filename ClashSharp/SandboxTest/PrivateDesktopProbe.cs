using System;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.SandboxValidation;

/// <summary>Runs only the developer probe on an unselected private desktop.</summary>
public sealed class PrivateDesktopProbe : IDisposable
{
    private readonly DesktopHandle _desktop;
    private readonly SafeWaitHandle _process;
    /// <summary>Gets the exact desktop assigned before the probe thread starts.</summary>
    public string DesktopName { get; }
    /// <summary>Gets the process created and owned by this runner.</summary>
    public uint ProcessId { get; }
    /// <summary>Gets whether the owned process has exited.</summary>
    public bool HasExited => WaitForSingleObject(_process, 0) == 0;
    /// <summary>Gets the native process exit code.</summary>
    public uint ExitCode => GetExitCodeProcess(_process, out uint code) ? code : throw new Win32Exception(Marshal.GetLastWin32Error());
    private PrivateDesktopProbe(DesktopHandle desktop, SafeWaitHandle process, string name, uint processId)
    {
        _desktop = desktop;
        _process = process;
        DesktopName = name;
        ProcessId = processId;
    }

    /// <summary>Assigns a private desktop before starting the probe; its UI thread verifies the assignment before opening windows.</summary>
    /// <param name="executable">Exact developer-only probe executable.</param>
    /// <param name="reportPath">Absolute output JSON filename.</param>
    public static PrivateDesktopProbe Start(string executable, string reportPath)
    {
        string name = "ClashSharpValidation-" + Guid.NewGuid().ToString("N");
        DesktopHandle desktop = CreateDesktop(name, null, IntPtr.Zero, 0, 0x01FF, IntPtr.Zero);
        if (desktop.IsInvalid) { desktop.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        PrivateDesktopProbe probe = null;
        try
        {
            var startup = new StartupInfo { Size = Marshal.SizeOf<StartupInfo>(), Desktop = "winsta0\\" + name };
            var command = new StringBuilder("\"" + executable + "\" \"" + reportPath + "\" \"" + name + "\"");
            if (!CreateProcess(executable, command, IntPtr.Zero, IntPtr.Zero, false, 0x08000004,
                IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out ProcessInfo process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            probe = new PrivateDesktopProbe(desktop, new SafeWaitHandle(process.Process, true), name, process.ProcessId);
            using var thread = new SafeWaitHandle(process.Thread, true);
            if (ResumeThread(thread) == uint.MaxValue) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
            return probe;
        }
        catch
        {
            if (probe is not null) { probe.Dispose(); }
            else { desktop.Dispose(); }
            throw;
        }
    }

    /// <summary>Stops only the owned probe if necessary, then releases its process and desktop handles.</summary>
    public void Dispose()
    {
        if (_process.IsClosed) { return; }
        try
        {
            if (!HasExited)
            {
                if (!TerminateProcess(_process, 1)) { throw new Win32Exception(Marshal.GetLastWin32Error()); }
                if (WaitForSingleObject(_process, 10000) != 0) { throw new TimeoutException("Probe shutdown timed out."); }
            }
        }
        finally { _process.Dispose(); _desktop.Dispose(); }
    }

    private sealed class DesktopHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public DesktopHandle() : base(true) { }
        protected override bool ReleaseHandle() { return CloseDesktop(handle); }
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size; public string Reserved; public string Desktop; public string Title;
        public int X, Y, Width, Height, XChars, YChars, Fill, Flags; public short ShowWindow, ReservedBytes;
        public IntPtr ReservedData, Input, Output, Error;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInfo { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
    [DllImport("user32.dll", EntryPoint = "CreateDesktopW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern DesktopHandle CreateDesktop(string name, string device, IntPtr mode, uint flags, uint access, IntPtr attributes);
    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcess(string application, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, IntPtr environment, string directory, ref StartupInfo startup, out ProcessInfo process);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(SafeWaitHandle process, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint ResumeThread(SafeWaitHandle thread);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(SafeWaitHandle process, uint exitCode);
    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(SafeWaitHandle process, out uint code);
}
