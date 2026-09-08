using System.ComponentModel;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Removes only the authenticated association's private runtime after SCM absence is verified.
/// The caller retains the protected machine-root lease and association until removal completes.
/// </summary>
internal sealed class WindowsServiceRuntimeCleanup
{
    internal const int MaximumEntries = 4096;
    private const int MaximumDepth = 64;

    internal void RemoveAndVerify(WindowsMachineDeploymentPlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        cancellationToken.ThrowIfCancellationRequested();
        string root = Path.Combine(plan.ServiceDataRoot, plan.Association.BuildServicePipeName());
        try
        {
            using SafeFileHandle parent = WindowsFileSystemNative
                .OpenOrdinaryDirectoryForMutationGuard(plan.ServiceDataRoot);
            if (!Exists(root))
            {
                return;
            }

            List<string> files = [];
            List<(string Path, int Depth)> directories = [];
            var pending = new Stack<(string Path, int Depth)>();
            pending.Push((root, 0));
            int entries = 0;
            while (pending.TryPop(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (item.Depth > MaximumDepth)
                {
                    throw new IOException("The service runtime exceeds its depth limit.");
                }

                using SafeFileHandle directory = WindowsFileSystemNative.OpenOrdinaryDirectory(item.Path);
                directories.Add(item);
                foreach (string entry in Directory.EnumerateFileSystemEntries(item.Path))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WindowsMachineDeploymentPlan.RequireExactDescendant(root, entry,
                        "installer.machine.service_runtime_cleanup_failed");
                    FileAttributes attributes = File.GetAttributes(entry);
                    if (++entries > MaximumEntries || (attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        throw new IOException("The service runtime contains an unsafe entry.");
                    }

                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push((entry, checked(item.Depth + 1)));
                    }
                    else
                    {
                        using SafeFileHandle file = WindowsFileSystemNative.OpenOrdinaryFile(entry);
                        if (WindowsFileSystemNative.GetLinkCount(file) != 1)
                        {
                            throw new IOException("The service runtime contains a multiply linked file.");
                        }
                        files.Add(entry);
                    }
                }
            }

            // Complete structural validation precedes deletion. The service is absent and the
            // protected parent prevents the interactive owner from replacing private descendants.
            foreach (string file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.SetAttributes(file, File.GetAttributes(file) & ~FileAttributes.ReadOnly);
                File.Delete(file);
            }

            foreach (var directory in directories.OrderByDescending(static item => item.Depth))
            {
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Delete(directory.Path, recursive: false);
            }

            if (Exists(root))
            {
                throw new IOException("The service runtime remains after removal.");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            throw new InstallerProtocolException("installer.machine.service_runtime_cleanup_failed", exception);
        }
    }

    private static bool Exists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
