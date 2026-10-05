using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Files;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Retains the original package and deployed machine file pins through restoration. Every source
/// path comes from the authenticated plan. The owner awaits SCM and transaction reconciliation
/// before disposal; cancellation cannot silently release these resources.
/// </summary>
internal sealed class WindowsMaintenancePayloadStateLease : IDisposable
{
    private readonly WindowsMachineDeploymentPlan _plan;
    private readonly string _packageRoot;
    private readonly List<WindowsLockedDirectory> _directories;
    private readonly List<SafeFileHandle> _files;
    private bool _disposed;

    internal WindowsMaintenancePayloadStateLease(WindowsMachineDeploymentPlan plan, string packageRoot,
        List<WindowsLockedDirectory> directories, List<SafeFileHandle> files,
        IReadOnlyList<WindowsMaintenanceFileFingerprint> fingerprints)
    {
        _plan = plan;
        _packageRoot = packageRoot;
        _directories = directories;
        _files = files;
        Fingerprints = fingerprints;
    }

    internal IReadOnlyList<WindowsMaintenanceFileFingerprint> Fingerprints { get; }

    internal void Reverify(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        foreach (WindowsLockedDirectory directory in _directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            directory.Reverify();
        }
        foreach (SafeFileHandle file in _files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = WindowsFileSystemNative.GetOrdinaryFileIdentity(file);
        }
        // Retained handles exclude writes/renames; independent plan-derived observation also
        // rejects changed tree shape and proves the currently named source/destination contents.
        IReadOnlyList<WindowsMaintenanceFileFingerprint> actual = new WindowsMaintenancePayloadStateReader()
            .Read(_plan, _packageRoot, cancellationToken);
        if (!Fingerprints.SequenceEqual(actual))
        {
            throw new InstallerProtocolException("installer.recovery.original_payload_changed");
        }
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        for (int index = _files.Count - 1; index >= 0; index--) { _files[index].Dispose(); }
        for (int index = _directories.Count - 1; index >= 0; index--) { _directories[index].Dispose(); }
    }
}
