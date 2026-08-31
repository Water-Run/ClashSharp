using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsTargetProfileNative
{
    string ReadProfileImagePath(string targetSid);

    string GetSystemDriveRoot();

    void VerifyOrdinaryDirectory(string path);
}

/// <summary>
/// Resolves an exact target SID through the 64-bit ProfileList registry view and permits only an
/// absolute local path or the single canonical %SystemDrive% prefix used by Windows profiles.
/// </summary>
internal sealed class WindowsTargetProfileResolver
{
    private readonly IWindowsTargetProfileNative _native;

    internal WindowsTargetProfileResolver()
        : this(WindowsTargetProfileNative.Instance)
    {
    }

    internal WindowsTargetProfileResolver(IWindowsTargetProfileNative native)
    {
        ArgumentNullException.ThrowIfNull(native);
        _native = native;
    }

    internal string Resolve(
        string targetSid,
        CancellationToken cancellationToken)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            string raw = _native.ReadProfileImagePath(targetSid);
            string systemDrive = CanonicalDriveRoot(_native.GetSystemDriveRoot());
            string expanded = ExpandSystemDriveOnly(raw, systemDrive);
            string canonical = CanonicalProfilePath(expanded);
            _native.VerifyOrdinaryDirectory(canonical);
            cancellationToken.ThrowIfCancellationRequested();
            return canonical;
        }
        catch (InstallerProtocolException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            throw new InstallerProtocolException(
                "installer.machine.target_profile_resolution_failed",
                exception);
        }
    }

    private static string ExpandSystemDriveOnly(string raw, string systemDrive)
    {
        if (string.IsNullOrWhiteSpace(raw)
            || raw.Length > 32_767
            || raw.Contains('"', StringComparison.Ordinal)
            || raw.Contains('/', StringComparison.Ordinal)
            || raw.Any(char.IsControl))
        {
            throw new InstallerProtocolException(
                "installer.machine.target_profile_path_invalid");
        }

        const string prefix = "%SystemDrive%";
        string expanded = raw;
        if (raw.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            if (raw.Length <= prefix.Length
                || raw[prefix.Length] != Path.DirectorySeparatorChar)
            {
                throw new InstallerProtocolException(
                    "installer.machine.target_profile_path_invalid");
            }

            expanded = string.Concat(
                systemDrive.TrimEnd(Path.DirectorySeparatorChar),
                raw.AsSpan(prefix.Length));
        }

        if (expanded.Contains('%', StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.target_profile_path_invalid");
        }

        return expanded;
    }

    private static string CanonicalProfilePath(string path)
    {
        if (!Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal)
            || path.Length < 4
            || !char.IsAsciiLetter(path[0])
            || path[1] != ':'
            || path[2] != Path.DirectorySeparatorChar
            || path.AsSpan(2).Contains(':')
            || path.IndexOfAny(['*', '?', '<', '>', '|']) >= 0)
        {
            throw new InstallerProtocolException(
                "installer.machine.target_profile_path_invalid");
        }

        string supplied = Path.TrimEndingDirectorySeparator(path);
        string canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? root = Path.GetPathRoot(canonical);
        if (root is null
            || root.Length != 3
            || string.Equals(canonical, root, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(supplied, canonical, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.machine.target_profile_path_invalid");
        }

        return canonical;
    }

    private static string CanonicalDriveRoot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)
            || !Path.IsPathFullyQualified(path)
            || path.StartsWith("\\\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\?\\", StringComparison.Ordinal)
            || path.StartsWith("\\\\.\\", StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.system_drive_invalid");
        }

        string full = Path.GetFullPath(path);
        string root = Path.GetPathRoot(full) ?? string.Empty;
        if (root.Length != 3
            || !char.IsAsciiLetter(root[0])
            || root[1] != ':'
            || root[2] != Path.DirectorySeparatorChar
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(full),
                Path.TrimEndingDirectorySeparator(root),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException(
                "installer.machine.system_drive_invalid");
        }

        return root;
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}

internal sealed class WindowsTargetProfileNative : IWindowsTargetProfileNative
{
    private const string ProfileListPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList";

    internal static WindowsTargetProfileNative Instance { get; } = new();

    private WindowsTargetProfileNative()
    {
    }

    public string ReadProfileImagePath(string targetSid)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        using RegistryKey localMachine = RegistryKey.OpenBaseKey(
            RegistryHive.LocalMachine,
            RegistryView.Registry64);
        using RegistryKey? profile = localMachine.OpenSubKey(
            string.Concat(ProfileListPath, "\\", targetSid),
            writable: false);
        if (profile is null)
        {
            throw new InstallerProtocolException(
                "installer.machine.target_profile_missing");
        }

        RegistryValueKind kind = profile.GetValueKind("ProfileImagePath");
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString)
            || profile.GetValue(
                "ProfileImagePath",
                defaultValue: null,
                RegistryValueOptions.DoNotExpandEnvironmentNames) is not string value)
        {
            throw new InstallerProtocolException(
                "installer.machine.target_profile_path_invalid");
        }

        return value;
    }

    public string GetSystemDriveRoot() =>
        Path.GetPathRoot(Environment.SystemDirectory)
        ?? throw new InstallerProtocolException(
            "installer.machine.system_drive_invalid");

    public void VerifyOrdinaryDirectory(string path)
    {
        using SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryDirectory(path);
    }
}
