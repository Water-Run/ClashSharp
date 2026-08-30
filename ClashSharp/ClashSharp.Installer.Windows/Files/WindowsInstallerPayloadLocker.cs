using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;

namespace ClashSharp.Installer.Windows.Files;

internal static class WindowsInstallerPayloadLocker
{
    internal static WindowsInstallerReleaseLease Lock(
        InstallerRequest request,
        InstallerReleaseManifest manifest,
        string payloadRoot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(manifest);
        request.Validate();
        manifest.Validate();
        cancellationToken.ThrowIfCancellationRequested();

        string root = Path.GetFullPath(payloadRoot);
        Dictionary<string, InstallerPayloadFileEntry> expectedFiles = manifest.Files
            .ToDictionary(static file => file.Path, StringComparer.Ordinal);
        HashSet<string> expectedDirectories = ExpectedDirectories(manifest);
        var directoryGuards = new List<WindowsLockedDirectory>();
        var lockedFiles = new List<WindowsLockedPayloadFile>();
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var actualFiles = new HashSet<string>(StringComparer.Ordinal);

        try
        {
            foreach (string ancestor in RenameableAncestors(root))
            {
                cancellationToken.ThrowIfCancellationRequested();
                directoryGuards.Add(WindowsLockedDirectory.Open(ancestor));
            }

            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                (string directory, int depth) = pending.Pop();
                foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    FileAttributes attributes = File.GetAttributes(entryPath);
                    if ((attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new InstallerProtocolException("installer.release.payload_reparse_rejected");
                    }

                    string relative = NormalizeRelativePath(root, entryPath);
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        int childDepth = checked(depth + 1);
                        if (childDepth > InstallerPayloadBudgets.MaximumDirectoryDepth
                            || !actualDirectories.Add(relative)
                            || actualDirectories.Count > InstallerPayloadBudgets.MaximumDirectoryCount
                            || !expectedDirectories.Contains(relative))
                        {
                            throw new InstallerProtocolException(
                                "installer.release.payload_directory_set_invalid");
                        }

                        directoryGuards.Add(WindowsLockedDirectory.Open(entryPath));
                        pending.Push((entryPath, childDepth));
                        continue;
                    }

                    if (!actualFiles.Add(relative)
                        || actualFiles.Count > InstallerPayloadBudgets.MaximumFileCount
                        || !expectedFiles.TryGetValue(relative, out InstallerPayloadFileEntry? expected))
                    {
                        throw new InstallerProtocolException("installer.release.payload_file_set_invalid");
                    }

                    lockedFiles.Add(WindowsLockedPayloadFile.Open(
                        Path.GetFullPath(entryPath),
                        expected,
                        cancellationToken));
                }
            }

            if (!actualFiles.SetEquals(expectedFiles.Keys)
                || !actualDirectories.SetEquals(expectedDirectories))
            {
                throw new InstallerProtocolException("installer.release.payload_file_set_invalid");
            }

            WindowsMsixIdentityVerifier.Verify(manifest, lockedFiles, cancellationToken);

            return new WindowsInstallerReleaseLease(
                request,
                manifest,
                root,
                lockedFiles,
                directoryGuards);
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or Win32Exception)
        {
            DisposeAll(lockedFiles, directoryGuards);
            throw new InstallerProtocolException("installer.release.payload_lock_failed", exception);
        }
        catch
        {
            DisposeAll(lockedFiles, directoryGuards);
            throw;
        }
    }

    internal static void VerifyExactShape(
        string payloadRoot,
        InstallerReleaseManifest manifest,
        CancellationToken cancellationToken)
    {
        HashSet<string> expectedFiles = manifest.Files
            .Select(static file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        HashSet<string> expectedDirectories = ExpectedDirectories(manifest);
        var actualFiles = new HashSet<string>(StringComparer.Ordinal);
        var actualDirectories = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((payloadRoot, 0));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            (string directory, int depth) = pending.Pop();
            foreach (string entryPath in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                FileAttributes attributes = File.GetAttributes(entryPath);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InstallerProtocolException("installer.release.payload_reparse_rejected");
                }

                string relative = NormalizeRelativePath(payloadRoot, entryPath);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    int childDepth = checked(depth + 1);
                    if (childDepth > InstallerPayloadBudgets.MaximumDirectoryDepth
                        || !actualDirectories.Add(relative)
                        || actualDirectories.Count > InstallerPayloadBudgets.MaximumDirectoryCount
                        || !expectedDirectories.Contains(relative))
                    {
                        throw new InstallerProtocolException(
                            "installer.release.payload_directory_set_invalid");
                    }

                    pending.Push((entryPath, childDepth));
                }
                else if (!actualFiles.Add(relative)
                    || actualFiles.Count > InstallerPayloadBudgets.MaximumFileCount
                    || !expectedFiles.Contains(relative))
                {
                    throw new InstallerProtocolException("installer.release.payload_file_set_invalid");
                }
            }
        }

        if (!actualFiles.SetEquals(expectedFiles)
            || !actualDirectories.SetEquals(expectedDirectories))
        {
            throw new InstallerProtocolException("installer.release.payload_file_set_invalid");
        }
    }

    private static HashSet<string> ExpectedDirectories(InstallerReleaseManifest manifest) =>
        manifest.Files
            .SelectMany(static file => ParentDirectories(file.Path))
            .ToHashSet(StringComparer.Ordinal);

    private static IEnumerable<string> ParentDirectories(string path)
    {
        int separator = path.IndexOf('/');
        while (separator >= 0)
        {
            yield return path[..separator];
            separator = path.IndexOf('/', separator + 1);
        }
    }

    private static IEnumerable<string> RenameableAncestors(string payloadRoot)
    {
        var ancestors = new List<string>();
        DirectoryInfo? current = new(payloadRoot);
        while (current.Parent is not null)
        {
            ancestors.Add(current.FullName);
            current = current.Parent;
        }

        ancestors.Reverse();
        return ancestors;
    }

    private static string NormalizeRelativePath(string root, string path)
    {
        string relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (Path.IsPathRooted(relative)
            || relative is "." or ".."
            || relative.StartsWith("../", StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.release.payload_path_escaped");
        }

        return relative.ToLowerInvariant();
    }

    private static void DisposeAll(
        List<WindowsLockedPayloadFile> lockedFiles,
        List<WindowsLockedDirectory> directoryGuards)
    {
        for (int index = lockedFiles.Count - 1; index >= 0; index--)
        {
            lockedFiles[index].Dispose();
        }

        for (int index = directoryGuards.Count - 1; index >= 0; index--)
        {
            directoryGuards[index].Dispose();
        }
    }
}
