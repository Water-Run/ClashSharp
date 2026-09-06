using System.Security.AccessControl;

namespace ClashSharp.Windows.FileSecurity;

internal interface IWindowsDirectoryReadLease : IDisposable
{
    WindowsDirectoryObservation Observe();
}

internal sealed record WindowsDirectoryObservation(
    bool IsDirectory,
    bool IsReparsePoint,
    WindowsDirectorySecuritySnapshot Security);

internal sealed record WindowsDirectorySecuritySnapshot(
    string? OwnerSid,
    bool HasDacl,
    bool DaclProtected,
    IReadOnlyList<WindowsDirectoryAce> AccessEntries);

internal sealed record WindowsDirectoryAce(
    string Sid,
    WindowsDirectoryAceKind Kind,
    int AccessMask,
    AceFlags Flags,
    bool IsObjectSpecific);

internal enum WindowsDirectoryAceKind
{
    Allow,
    Deny,
    Unsupported,
}
