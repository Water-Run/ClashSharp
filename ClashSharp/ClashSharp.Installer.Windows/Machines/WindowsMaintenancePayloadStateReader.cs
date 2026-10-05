using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Reads fixed plan-derived payload files and compares them with the healthy registered package.
/// Ordinary directory and file handles pin this observation; recovery separately retains original
/// leases through restoration. No caller-provided fingerprint path is used to open a file.
/// </summary>
internal sealed class WindowsMaintenancePayloadStateReader : IWindowsMaintenancePayloadStateReader
{
    public IReadOnlyList<WindowsMaintenanceFileFingerprint> Read(WindowsMachineDeploymentPlan plan,
        string originalPackageRoot, CancellationToken cancellationToken)
    {
        using WindowsMaintenancePayloadStateLease lease = Acquire(plan, originalPackageRoot, cancellationToken);
        return lease.Fingerprints;
    }

    internal WindowsMaintenancePayloadStateLease Acquire(WindowsMachineDeploymentPlan plan,
        string originalPackageRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentException.ThrowIfNullOrWhiteSpace(originalPackageRoot);
        plan.Validate();
        if (!Path.IsPathFullyQualified(originalPackageRoot) || plan.Request.Operation != InstallerOperation.Repair)
        {
            throw new InstallerProtocolException("installer.recovery.original_payload_invalid");
        }
        string packageRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(originalPackageRoot));
        var directories = new List<WindowsLockedDirectory>();
        var pinnedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var handles = new List<SafeFileHandle>();
        var fingerprints = new List<WindowsMaintenanceFileFingerprint>();
        bool returned = false;
        try
        {
            PinAncestors(packageRoot, directories, pinnedPaths, cancellationToken);
            PinAncestors(plan.CurrentRoot, directories, pinnedPaths, cancellationToken);
            RequireExactCurrentTree(plan, directories, pinnedPaths, cancellationToken);
            foreach (WindowsMachinePayloadTarget target in plan.PayloadTargets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string packagePath = Path.GetFullPath(Path.Combine(packageRoot, target.Source.Path.Replace('/', Path.DirectorySeparatorChar)));
                if (!packagePath.StartsWith(packageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InstallerProtocolException("installer.recovery.original_payload_invalid");
                }
                PinAncestors(Path.GetDirectoryName(packagePath)!, directories, pinnedPaths, cancellationToken);
                SafeFileHandle source = WindowsFileSystemNative.OpenOrdinaryFile(packagePath);
                handles.Add(source);
                SafeFileHandle destination = WindowsFileSystemNative.OpenOrdinaryFile(target.DestinationPath);
                handles.Add(destination);
                long length = RandomAccess.GetLength(source);
                if (length <= 0 || length > InstallerPayloadBudgets.MaximumMachinePayloadBytes || RandomAccess.GetLength(destination) != length)
                {
                    throw new InstallerProtocolException("installer.recovery.original_payload_changed");
                }
                new InstallerMachinePayloadFileEntry(target.Source.Path, length, new string('0', 64)).Validate();
                string hash = Hash(source, length, cancellationToken);
                new InstallerMachinePayloadFileEntry(target.Source.Path, length, hash).Validate();
                if (Hash(destination, length, cancellationToken) != hash)
                {
                    throw new InstallerProtocolException("installer.recovery.original_payload_changed");
                }
                fingerprints.Add(new(target.Source.Path, target.RelativeTargetPath.Replace('\\', '/'), length, hash));
            }
            foreach (WindowsLockedDirectory directory in directories)
            {
                cancellationToken.ThrowIfCancellationRequested();
                directory.Reverify();
            }
            var lease = new WindowsMaintenancePayloadStateLease(plan, packageRoot,
                directories, handles, Array.AsReadOnly(fingerprints.ToArray()));
            returned = true;
            return lease;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.recovery.original_payload_inspection_failed", exception);
        }
        finally
        {
            if (!returned)
            {
                for (int index = handles.Count - 1; index >= 0; index--) { handles[index].Dispose(); }
                for (int index = directories.Count - 1; index >= 0; index--) { directories[index].Dispose(); }
            }
        }
    }

    private static void RequireExactCurrentTree(WindowsMachineDeploymentPlan plan,
        List<WindowsLockedDirectory> directories, HashSet<string> pinnedPaths, CancellationToken cancellationToken)
    {
        HashSet<string> expected = plan.PayloadTargets.Select(target => target.RelativeTargetPath.Replace('\\', '/').ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var expectedDirectories = new HashSet<string>(StringComparer.Ordinal);
        foreach (string path in expected)
        {
            int separator = path.IndexOf('/');
            while (separator >= 0)
            {
                expectedDirectories.Add(path[..separator]);
                separator = path.IndexOf('/', separator + 1);
            }
        }
        var files = new HashSet<string>(StringComparer.Ordinal);
        var folders = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(plan.CurrentRoot);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entry);
                string relative = Path.GetRelativePath(plan.CurrentRoot, entry).Replace('\\', '/').ToLowerInvariant();
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InstallerProtocolException("installer.recovery.original_payload_invalid");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Contains(relative) || !folders.Add(relative))
                    {
                        throw new InstallerProtocolException("installer.recovery.original_payload_changed");
                    }
                    PinAncestors(entry, directories, pinnedPaths, cancellationToken);
                    pending.Push(entry);
                }
                else if (!expected.Contains(relative) || !files.Add(relative))
                {
                    throw new InstallerProtocolException("installer.recovery.original_payload_changed");
                }
            }
        }
        if (!files.SetEquals(expected) || !folders.SetEquals(expectedDirectories))
        {
            throw new InstallerProtocolException("installer.recovery.original_payload_changed");
        }
    }

    private static void PinAncestors(string path, List<WindowsLockedDirectory> directories,
        HashSet<string> pinnedPaths, CancellationToken cancellationToken)
    {
        var ancestors = new Stack<string>();
        var current = new DirectoryInfo(path);
        while (current.Parent is not null) { ancestors.Push(current.FullName); current = current.Parent; }
        foreach (string ancestor in ancestors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pinnedPaths.Add(ancestor)) { directories.Add(WindowsLockedDirectory.OpenForObservation(ancestor)); }
        }
    }

    private static string Hash(SafeFileHandle handle, long length, CancellationToken cancellationToken)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                if (count == 0) { throw new InstallerProtocolException("installer.recovery.original_payload_changed"); }
                hash.AppendData(buffer.AsSpan(0, count));
                offset += count;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return Convert.ToHexStringLower(hash.GetHashAndReset());
        }
        finally { CryptographicOperations.ZeroMemory(buffer); ArrayPool<byte>.Shared.Return(buffer); }
    }
}
