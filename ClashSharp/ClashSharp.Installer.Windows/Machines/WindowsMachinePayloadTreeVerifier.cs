using System.Buffers;
using System.ComponentModel;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

internal enum WindowsMachinePayloadTreeStatus
{
    Missing,
    ExactMatch,
    Invalid,
}

internal interface IWindowsMachinePayloadTreeInspector
{
    WindowsMachinePayloadTreeStatus Inspect(
        WindowsMachineDeploymentPlan plan,
        string root,
        CancellationToken cancellationToken);
}

/// <summary>
/// Inspects one fixed machine payload slot without following reparse points and verifies the exact
/// file/directory set, lengths, and hashes while delete-sharing is withheld from directory handles.
/// </summary>
internal sealed class WindowsMachinePayloadTreeVerifier : IWindowsMachinePayloadTreeInspector
{
    public WindowsMachinePayloadTreeStatus Inspect(
        WindowsMachineDeploymentPlan plan,
        string root,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        RequireKnownSlot(plan, root);
        cancellationToken.ThrowIfCancellationRequested();

        SafeFileHandle? rootHandle = null;
        try
        {
            rootHandle = WindowsFileSystemNative.OpenOrdinaryDirectory(root);
        }
        catch (Exception exception) when (IsMissing(exception))
        {
            return WindowsMachinePayloadTreeStatus.Missing;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return WindowsMachinePayloadTreeStatus.Invalid;
        }

        var directoryHandles = new List<SafeFileHandle> { rootHandle };
        try
        {
            return InspectOpenTree(plan, root, directoryHandles, cancellationToken)
                ? WindowsMachinePayloadTreeStatus.ExactMatch
                : WindowsMachinePayloadTreeStatus.Invalid;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsRecoverable(exception))
        {
            return WindowsMachinePayloadTreeStatus.Invalid;
        }
        finally
        {
            for (int index = directoryHandles.Count - 1; index >= 0; index--)
            {
                directoryHandles[index].Dispose();
            }
        }
    }

    internal void VerifyExact(
        WindowsMachineDeploymentPlan plan,
        string root,
        CancellationToken cancellationToken)
    {
        if (Inspect(plan, root, cancellationToken)
            != WindowsMachinePayloadTreeStatus.ExactMatch)
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_tree_verification_failed");
        }
    }

    internal void VerifyAbsent(
        WindowsMachineDeploymentPlan plan,
        string root,
        CancellationToken cancellationToken)
    {
        if (Inspect(plan, root, cancellationToken)
            != WindowsMachinePayloadTreeStatus.Missing)
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_tree_removal_failed");
        }
    }

    private static bool InspectOpenTree(
        WindowsMachineDeploymentPlan plan,
        string root,
        List<SafeFileHandle> directoryHandles,
        CancellationToken cancellationToken)
    {
        Dictionary<string, InstallerMachinePayloadFileEntry> expectedFiles = plan.PayloadTargets
            .ToDictionary(
                static target => NormalizeRelative(target.RelativeTargetPath),
                static target => target.Source,
                StringComparer.Ordinal);
        HashSet<string> expectedDirectories = expectedFiles.Keys
            .SelectMany(ParentDirectories)
            .ToHashSet(StringComparer.Ordinal);
        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string directory = pending.Pop();
            foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    return false;
                }

                string relative = Relative(root, entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (!expectedDirectories.Contains(relative)
                        || !actualDirectories.Add(relative)
                        || actualDirectories.Count > expectedDirectories.Count)
                    {
                        return false;
                    }

                    directoryHandles.Add(
                        WindowsFileSystemNative.OpenOrdinaryDirectory(entryPath));
                    pending.Push(entryPath);
                    continue;
                }

                if (!expectedFiles.TryGetValue(
                        relative,
                        out InstallerMachinePayloadFileEntry? expected)
                    || !actualFiles.Add(relative)
                    || actualFiles.Count > expectedFiles.Count
                    || !VerifyFile(entryPath, expected, cancellationToken))
                {
                    return false;
                }
            }
        }

        return actualFiles.SetEquals(expectedFiles.Keys)
            && actualDirectories.SetEquals(expectedDirectories);
    }

    private static bool VerifyFile(
        string path,
        InstallerMachinePayloadFileEntry expected,
        CancellationToken cancellationToken)
    {
        using SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryFile(path);
        if (RandomAccess.GetLength(handle) != expected.Length)
        {
            return false;
        }

        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long offset = 0;
            while (offset < expected.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, expected.Length - offset);
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
                if (read == 0)
                {
                    return false;
                }

                hash.AppendData(buffer.AsSpan(0, read));
                offset = checked(offset + read);
            }

            byte[] digest = hash.GetHashAndReset();
            try
            {
                return string.Equals(
                    Convert.ToHexStringLower(digest),
                    expected.Sha256,
                    StringComparison.Ordinal);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static string Relative(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path);
        if (Path.IsPathFullyQualified(relative)
            || relative is "." or ".."
            || relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_tree_path_invalid");
        }

        return NormalizeRelative(relative);
    }

    private static string NormalizeRelative(string path) =>
        path.Replace(Path.DirectorySeparatorChar, '/').ToLowerInvariant();

    private static IEnumerable<string> ParentDirectories(string path)
    {
        int separator = path.IndexOf('/');
        while (separator >= 0)
        {
            yield return path[..separator];
            separator = path.IndexOf('/', separator + 1);
        }
    }

    private static void RequireKnownSlot(
        WindowsMachineDeploymentPlan plan,
        string root)
    {
        if (string.IsNullOrWhiteSpace(root)
            || !string.Equals(root, Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)
            || !(string.Equals(root, plan.CurrentRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(root, plan.StagingRoot, StringComparison.OrdinalIgnoreCase)
                || string.Equals(root, plan.PreviousRoot, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_slot_invalid");
        }
    }

    private static bool IsMissing(Exception exception) =>
        exception is FileNotFoundException or DirectoryNotFoundException
        || exception is Win32Exception { NativeErrorCode: 2 or 3 };

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);
}
