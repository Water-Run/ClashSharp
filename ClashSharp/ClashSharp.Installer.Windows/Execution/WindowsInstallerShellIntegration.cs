using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;
using ClashSharp.Installer.Contracts;
using ClashSharp.Windows.FileSecurity;

namespace ClashSharp.Installer.Windows.Execution;

internal interface IWindowsInstallerShellIntegration
{
    Task ApplyAsync(InstallerPreferences preferences, string applicationId, string iconPath, CancellationToken cancellationToken);
    Task RemoveAsync(string applicationId, CancellationToken cancellationToken);
}

/// <summary>Creates current-user native shell links and preserves every unowned name collision.</summary>
internal sealed class WindowsInstallerShellIntegration : IWindowsInstallerShellIntegration
{
    private const string OwnerDescription = "Clash# application shortcut";
    private static readonly PropertyKey ApplicationIdKey = new(new("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public Task ApplyAsync(InstallerPreferences preferences, string applicationId, string iconPath, CancellationToken cancellationToken) =>
        RunStaAsync(() =>
        {
            SetShortcut(Environment.SpecialFolder.DesktopDirectory, preferences.CreateDesktopShortcut, applicationId, iconPath, cancellationToken);
        }, cancellationToken);

    public Task RemoveAsync(string applicationId, CancellationToken cancellationToken) => RunStaAsync(() =>
    {
        SetShortcut(Environment.SpecialFolder.DesktopDirectory, false, applicationId, null, cancellationToken);
    }, cancellationToken);

    private static void SetShortcut(Environment.SpecialFolder kind, bool enabled, string applicationId, string? iconPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string folder = Environment.GetFolderPath(kind);
        if (string.IsNullOrEmpty(folder) || !Path.IsPathFullyQualified(folder)) { throw Failure("folder_unavailable"); }
        if (!Directory.Exists(folder)) { Directory.CreateDirectory(folder); }
        using WindowsDirectoryReadLease directory = WindowsDirectoryReadLease.Open(folder);
        WindowsDirectoryObservation observation = directory.Observe();
        if (!observation.IsDirectory || observation.IsReparsePoint) { throw Failure("folder_reparse"); }
        string destination = Path.Combine(folder, "Clash#.lnk");
        if (File.Exists(destination))
        {
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) != 0 || !IsOwned(destination, applicationId))
            {
                if (enabled) { throw Failure("shortcut_name_conflict"); }
                return;
            }
            if (!enabled) { File.Delete(destination); return; }
        }
        else if (!enabled) { return; }
        cancellationToken.ThrowIfCancellationRequested();
        string temporary = Path.Combine(folder, ".Clash#-" + Guid.NewGuid().ToString("N") + ".lnk");
        object native = new ShellLink();
        try
        {
            IShellLinkW link = (IShellLinkW)native;
            link.SetPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"));
            link.SetArguments("shell:AppsFolder\\" + applicationId);
            link.SetDescription(OwnerDescription);
            if (iconPath is not null) { link.SetIconLocation(iconPath, 0); }
            var value = new PropertyVariant { Type = 31, Pointer = Marshal.StringToCoTaskMemUni(applicationId) };
            try
            {
                IPropertyStore properties = (IPropertyStore)native;
                PropertyKey key = ApplicationIdKey;
                properties.SetValue(ref key, ref value);
                properties.Commit();
            }
            finally { Marshal.ThrowExceptionForHR(ClearVariant(ref value)); }
            ((IPersistFile)native).Save(temporary, true);
            if (!IsOwned(temporary, applicationId)) { throw Failure("shortcut_verification_failed"); }
            cancellationToken.ThrowIfCancellationRequested();
            // Recheck immediately before replacement: manual or foreign shortcuts are never adopted.
            if (File.Exists(destination) && !IsOwned(destination, applicationId)) { throw Failure("shortcut_changed"); }
            File.Move(temporary, destination, overwrite: true);
            if (!IsOwned(destination, applicationId)) { throw Failure("shortcut_verification_failed"); }
        }
        finally
        {
            Marshal.FinalReleaseComObject(native);
            if (File.Exists(temporary)) { File.Delete(temporary); }
        }
    }

    private static bool IsOwned(string path, string applicationId)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) { return false; }
        object native = new ShellLink();
        try
        {
            ((IPersistFile)native).Load(path, 0);
            IShellLinkW link = (IShellLinkW)native;
            StringBuilder description = new(256), arguments = new(1024), target = new(32768);
            link.GetDescription(description, description.Capacity);
            link.GetArguments(arguments, arguments.Capacity);
            link.GetPath(target, target.Capacity, 0, 4);
            if (!string.Equals(description.ToString(), OwnerDescription, StringComparison.Ordinal)
                || !string.Equals(arguments.ToString(), "shell:AppsFolder\\" + applicationId, StringComparison.Ordinal)
                || !string.Equals(target.ToString(), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), StringComparison.OrdinalIgnoreCase)) { return false; }
            PropertyKey key = ApplicationIdKey;
            ((IPropertyStore)native).GetValue(ref key, out PropertyVariant value);
            try { return value.Type == 31 && string.Equals(Marshal.PtrToStringUni(value.Pointer), applicationId, StringComparison.Ordinal); }
            finally { Marshal.ThrowExceptionForHR(ClearVariant(ref value)); }
        }
        catch (Exception exception) when (!InstallerExceptionPolicy.IsProcessFatal(exception)
            && exception is COMException or IOException or UnauthorizedAccessException)
        { return false; }
        finally { Marshal.FinalReleaseComObject(native); }
    }

    private static Task RunStaAsync(Action operation, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Thread thread = new(() =>
        {
            try { cancellationToken.ThrowIfCancellationRequested(); operation(); completion.TrySetResult(); }
            // This is an exception-transport boundary. Fatal graphs remain faulted on the owned
            // task and are classified by its awaiting installer owner, never swallowed here.
            catch (Exception exception) { completion.TrySetException(exception); }
        })
        { IsBackground = true, Name = "Clash# installation shell setup" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static InstallerProtocolException Failure(string code) => new("installer.preferences." + code);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid format, uint id)
    {
        internal Guid Format = format;
        internal uint Id = id;
    }

    // x64 PROPVARIANT has an eight-byte header and a sixteen-byte union, including
    // the native fields not used for VT_LPWSTR. COM still writes the entire value.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropertyVariant
    {
        [FieldOffset(0)] internal ushort Type;
        [FieldOffset(8)] internal nint Pointer;
    }

    [DllImport("ole32.dll", EntryPoint = "PropVariantClear", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int ClearVariant(ref PropertyVariant value);

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class ShellLink { }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int length, nint findData, uint flags);
        void GetIDList(out nint idList);
        void SetIDList(nint idList);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder description, int length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int length);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int length);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);
        void GetHotkey(out short hotkey);
        void SetHotkey(short hotkey);
        void GetShowCmd(out int command);
        void SetShowCmd(int command);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder icon, int length, out int index);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string icon, int index);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);
        void Resolve(nint window, uint flags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string path);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        void GetCount(out uint count);
        void GetAt(uint index, out PropertyKey key);
        void GetValue(ref PropertyKey key, out PropertyVariant value);
        void SetValue(ref PropertyKey key, ref PropertyVariant value);
        void Commit();
    }
}
