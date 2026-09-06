using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using ClashSharp.Installer.Contracts;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Opens a fixed global kernel mutex with an exact SYSTEM/Administrators DACL. Existing objects
/// are verified on their open handle; object-name presence never grants installation authority.
/// </summary>
internal sealed class WindowsInstallerAuthorityMutex : IWindowsInstallerAuthorityMutex
{
    internal const string ObjectName = @"Global\ClashSharp.Installer.Authority.v1";
    private const int AllAccess = 0x001F_0001;
    private const string SystemSid = "S-1-5-18";
    private const string AdministratorsSid = "S-1-5-32-544";
    private readonly SafeWaitHandle _handle;

    // Takes ownership of an already opened handle. Production creation must go through Open.
    internal WindowsInstallerAuthorityMutex(SafeWaitHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        _handle = handle;
    }

    internal static IWindowsInstallerAuthorityMutex Open()
    {
        RawSecurityDescriptor security = CreateSecurity();
        byte[] bytes = new byte[security.BinaryLength];
        security.GetBinaryForm(bytes, 0);
        nint descriptor = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, descriptor, bytes.Length);
            var attributes = new SecurityAttributes
            {
                Length = Marshal.SizeOf<SecurityAttributes>(),
                SecurityDescriptor = descriptor,
                InheritHandle = false,
            };
            SafeWaitHandle handle = CreateMutexExW(ref attributes, ObjectName, 0, AllAccess);
            if (handle.IsInvalid)
            {
                int error = Marshal.GetLastPInvokeError();
                handle.Dispose();
                throw new InstallerProtocolException(
                    "installer.authority.lock_open_failed",
                    new Win32Exception(error));
            }

            try
            {
                ValidateSecurity(ReadSecurity(handle));
                return new WindowsInstallerAuthorityMutex(handle);
            }
            catch
            {
                handle.Dispose();
                throw;
            }
        }
        finally
        {
            Marshal.FreeHGlobal(descriptor);
        }
    }

    public bool TryAcquire()
    {
        uint result = WaitForSingleObject(_handle, 0);
        return result switch
        {
            0 or 0x80 => true, // An abandoned owner still requires the helper's normal durable reload.
            0x102 => false,
            _ => throw new InstallerProtocolException(
                "installer.authority.lock_wait_failed",
                new Win32Exception(Marshal.GetLastPInvokeError())),
        };
    }

    public void Release()
    {
        if (!ReleaseMutex(_handle))
        {
            throw new InstallerProtocolException(
                "installer.authority.lock_release_failed",
                new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    public void Dispose() => _handle.Dispose();

    internal static RawSecurityDescriptor CreateSecurity()
    {
        var administrators = new SecurityIdentifier(AdministratorsSid);
        var acl = new RawAcl(GenericAcl.AclRevision, 2);
        acl.InsertAce(0, new CommonAce(
            AceFlags.None, AceQualifier.AccessAllowed, AllAccess,
            new SecurityIdentifier(SystemSid), isCallback: false, opaque: null));
        acl.InsertAce(1, new CommonAce(
            AceFlags.None, AceQualifier.AccessAllowed, AllAccess,
            administrators, isCallback: false, opaque: null));
        return new RawSecurityDescriptor(
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected,
            administrators, administrators, systemAcl: null, acl);
    }

    internal static void ValidateSecurity(RawSecurityDescriptor security)
    {
        ArgumentNullException.ThrowIfNull(security);
        const ControlFlags required =
            ControlFlags.DiscretionaryAclPresent | ControlFlags.DiscretionaryAclProtected;
        if ((security.ControlFlags & required) != required
            || security.Owner?.Value != AdministratorsSid
            || security.DiscretionaryAcl is not { Count: 2 } acl
            || !HasExactRule(acl, SystemSid)
            || !HasExactRule(acl, AdministratorsSid))
        {
            throw new InstallerProtocolException("installer.authority.lock_acl_invalid");
        }
    }

    private static bool HasExactRule(RawAcl acl, string sid) =>
        acl.Cast<GenericAce>().Count(ace => ace is CommonAce
        {
            AceQualifier: AceQualifier.AccessAllowed,
            AceFlags: AceFlags.None,
            IsCallback: false,
            AccessMask: AllAccess,
        } common && common.SecurityIdentifier.Value == sid) == 1;

    private static RawSecurityDescriptor ReadSecurity(SafeWaitHandle handle)
    {
        uint error = GetSecurityInfo(handle, 6, 0x5, out _, out _, out _, out _, out nint descriptor);
        try
        {
            if (error != 0 || descriptor == 0)
            {
                throw new InstallerProtocolException(
                    "installer.authority.lock_acl_read_failed",
                    new Win32Exception(unchecked((int)error)));
            }

            uint length = GetSecurityDescriptorLength(descriptor);
            if (length is 0 or > 8192)
            {
                throw new InstallerProtocolException("installer.authority.lock_acl_invalid");
            }

            byte[] bytes = new byte[checked((int)length)];
            Marshal.Copy(descriptor, bytes, 0, bytes.Length);
            return new RawSecurityDescriptor(bytes, 0);
        }
        finally
        {
            if (descriptor != 0)
            {
                _ = LocalFree(descriptor);
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        internal int Length;
        internal nint SecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        internal bool InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern SafeWaitHandle CreateMutexExW(
        ref SecurityAttributes attributes, string name, uint flags, uint desiredAccess);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint WaitForSingleObject(SafeWaitHandle handle, uint milliseconds);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseMutex(SafeWaitHandle handle);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityInfo(
        SafeWaitHandle handle, int objectType, uint securityInformation,
        out nint owner, out nint group, out nint dacl, out nint sacl, out nint descriptor);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetSecurityDescriptorLength(nint descriptor);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern nint LocalFree(nint memory);
}
