using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Performs fixed-slot filesystem writes only beneath an already-created protected machine root.
/// Root creation and ACL authority are intentionally a separate prerequisite.
/// </summary>
internal sealed class WindowsMachinePayloadSlotNative : IWindowsMachinePayloadSlotNative
{
    private const uint MoveFileWriteThrough = 0x0000_0008;
    private const int MaximumTreeEntries = 64;
    private const int MaximumTreeDepth = 4;
    private readonly WindowsMachinePayloadTreeVerifier _verifier = new();

    internal static WindowsMachinePayloadSlotNative Instance { get; } = new();

    private WindowsMachinePayloadSlotNative()
    {
    }

    public void ResetStaging(WindowsMachineDeploymentPlan plan)
    {
        ValidatePlan(plan);
        using SafeFileHandle rootGuard = OpenRootGuard(plan);
        DeleteTreeSafely(plan, plan.StagingRoot);
        Directory.CreateDirectory(plan.StagingRoot);
        using SafeFileHandle staging = WindowsFileSystemNative.OpenOrdinaryDirectory(
            plan.StagingRoot);
        foreach (string directory in ExpectedStagingDirectories(plan))
        {
            Directory.CreateDirectory(directory);
            using SafeFileHandle child = WindowsFileSystemNative.OpenOrdinaryDirectory(directory);
        }
    }

    public IWindowsMachineStagingFile CreateStagingFile(
        WindowsMachineDeploymentPlan plan,
        WindowsMachinePayloadTarget target)
    {
        ValidatePlan(plan);
        ArgumentNullException.ThrowIfNull(target);
        WindowsMachinePayloadTarget exact = plan.PayloadTargets.SingleOrDefault(candidate =>
                candidate == target)
            ?? throw new InstallerProtocolException(
                "installer.machine.payload_target_invalid");
        string path = StagingPath(plan, exact);
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrWhiteSpace(parent))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_target_invalid");
        }

        using SafeFileHandle rootGuard = OpenRootGuard(plan);
        using SafeFileHandle stagingGuard =
            WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(plan.StagingRoot);
        using SafeFileHandle parentGuard =
            WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(parent);
        var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        return new WindowsMachineStagingFile(stream);
    }

    public void CompleteStagingTree(WindowsMachineDeploymentPlan plan)
    {
        ValidatePlan(plan);
        using SafeFileHandle rootGuard = OpenRootGuard(plan);
        _verifier.VerifyExact(plan, plan.StagingRoot, CancellationToken.None);
    }

    public void PromoteStaging(WindowsMachineDeploymentPlan plan)
    {
        ValidatePlan(plan);
        using SafeFileHandle rootGuard = OpenRootGuard(plan);
        _verifier.VerifyExact(plan, plan.StagingRoot, CancellationToken.None);
        DeleteTreeSafely(plan, plan.PreviousRoot);

        bool movedCurrent = false;
        if (SlotExists(plan.CurrentRoot))
        {
            ValidateSafeTree(plan, plan.CurrentRoot);
            Move(plan.CurrentRoot, plan.PreviousRoot);
            movedCurrent = true;
        }

        try
        {
            Move(plan.StagingRoot, plan.CurrentRoot);
        }
        catch (Exception promotionFailure)
        {
            if (movedCurrent
                && !SlotExists(plan.CurrentRoot)
                && SlotExists(plan.PreviousRoot))
            {
                try
                {
                    Move(plan.PreviousRoot, plan.CurrentRoot);
                }
                catch (Exception rollbackFailure)
                {
                    throw new InstallerStateUncertainException(
                        "installer.machine.payload_state_uncertain")
                    {
                        Data =
                        {
                            ["promotionFailureType"] = promotionFailure.GetType().FullName,
                            ["rollbackFailureType"] = rollbackFailure.GetType().FullName,
                        },
                    };
                }
            }

            ExceptionDispatchInfo.Capture(promotionFailure).Throw();
        }
    }

    public void CleanupAfterPromotion(WindowsMachineDeploymentPlan plan)
    {
        ValidatePlan(plan);
        using SafeFileHandle rootGuard = OpenRootGuard(plan);
        DeleteTreeSafely(plan, plan.StagingRoot);
        DeleteTreeSafely(plan, plan.PreviousRoot);
    }

    public void RemoveAllSlots(WindowsMachineDeploymentPlan plan)
    {
        ValidatePlan(plan);
        using SafeFileHandle rootGuard = OpenRootGuard(plan);
        DeleteTreeSafely(plan, plan.StagingRoot);
        DeleteTreeSafely(plan, plan.PreviousRoot);
        DeleteTreeSafely(plan, plan.CurrentRoot);
    }

    private static void DeleteTreeSafely(
        WindowsMachineDeploymentPlan plan,
        string root)
    {
        RequireSlot(plan, root);
        if (!SlotExists(root))
        {
            return;
        }

        List<string> files = [];
        List<(string Path, int Depth)> directories = [];
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (string directory, int depth) = pending.Pop();
            if (depth > MaximumTreeDepth)
            {
                throw new InstallerProtocolException(
                    "installer.machine.payload_tree_unsafe");
            }

            using (SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryDirectory(
                       directory))
            {
            }

            directories.Add((directory, depth));
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0
                    || files.Count + directories.Count >= MaximumTreeEntries)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.payload_tree_unsafe");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, checked(depth + 1)));
                }
                else
                {
                    using SafeFileHandle file = WindowsFileSystemNative.OpenOrdinaryFile(entry);
                    files.Add(entry);
                }
            }
        }

        foreach (string file in files)
        {
            File.Delete(file);
        }

        foreach ((string directory, _) in directories
                     .OrderByDescending(static item => item.Depth))
        {
            Directory.Delete(directory, recursive: false);
        }
    }

    private static void ValidateSafeTree(
        WindowsMachineDeploymentPlan plan,
        string root)
    {
        RequireSlot(plan, root);
        if (!SlotExists(root))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_tree_unsafe");
        }

        int entries = 0;
        var pending = new Stack<(string Path, int Depth)>();
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            (string directory, int depth) = pending.Pop();
            if (depth > MaximumTreeDepth)
            {
                throw new InstallerProtocolException(
                    "installer.machine.payload_tree_unsafe");
            }

            using SafeFileHandle handle = WindowsFileSystemNative.OpenOrdinaryDirectory(directory);
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                entries++;
                FileAttributes attributes = File.GetAttributes(entry);
                if (entries > MaximumTreeEntries
                    || (attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.payload_tree_unsafe");
                }

                if ((attributes & FileAttributes.Directory) != 0)
                {
                    pending.Push((entry, checked(depth + 1)));
                }
                else
                {
                    using SafeFileHandle file = WindowsFileSystemNative.OpenOrdinaryFile(entry);
                }
            }
        }
    }

    private static IEnumerable<string> ExpectedStagingDirectories(
        WindowsMachineDeploymentPlan plan) =>
        plan.PayloadTargets
            .Select(target => Path.GetDirectoryName(StagingPath(plan, target)))
            .OfType<string>()
            .Where(path => !string.Equals(
                path,
                plan.StagingRoot,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static path => path.Count(character =>
                character == Path.DirectorySeparatorChar));

    private static string StagingPath(
        WindowsMachineDeploymentPlan plan,
        WindowsMachinePayloadTarget target)
    {
        string path = Path.GetFullPath(Path.Combine(
            plan.StagingRoot,
            target.RelativeTargetPath));
        WindowsMachineDeploymentPlan.RequireExactDescendant(
            plan.StagingRoot,
            path,
            "installer.machine.payload_target_invalid");
        return path;
    }

    private static SafeFileHandle OpenRootGuard(WindowsMachineDeploymentPlan plan) =>
        WindowsFileSystemNative.OpenOrdinaryDirectoryForMutationGuard(plan.MachineRoot);

    private static void Move(string source, string destination)
    {
        if (!MoveFileEx(source, destination, MoveFileWriteThrough))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    private static bool SlotExists(string path)
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

    private static void ValidatePlan(WindowsMachineDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
    }

    private static void RequireSlot(
        WindowsMachineDeploymentPlan plan,
        string root)
    {
        if (!(string.Equals(root, plan.CurrentRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, plan.StagingRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(root, plan.PreviousRoot, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_slot_invalid");
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileEx(
        string existingFileName,
        string newFileName,
        uint flags);
}

internal sealed class WindowsMachineStagingFile : IWindowsMachineStagingFile
{
    private readonly FileStream _stream;
    private bool _disposed;

    internal WindowsMachineStagingFile(FileStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    public Stream Content
    {
        get
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return _stream;
        }
    }

    public void FlushToDisk()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _stream.Flush(flushToDisk: true);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        await _stream.DisposeAsync().ConfigureAwait(false);
        _disposed = true;
    }
}
