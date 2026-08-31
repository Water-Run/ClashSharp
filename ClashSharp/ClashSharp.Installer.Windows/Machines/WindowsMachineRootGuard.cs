using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineRootGuard : IDisposable
{
    Task EnsureProtectedAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken);
}

/// <summary>
/// Creates, validates, and pins both fixed machine roots. Ancestors must be trusted rename anchors;
/// the Service and MihomoService roots must have the exact protected target-readable ACL.
/// </summary>
internal sealed class WindowsMachineRootGuard : IWindowsMachineRootGuard
{
    private readonly object _gate = new();
    private readonly WindowsMachineDeploymentPlan _plan;
    private readonly IWindowsInstallerDirectoryNative _native;
    private readonly bool _createMissingProtectedDirectories;
    private List<(DirectorySpec Spec, IWindowsInstallerDirectoryLease Lease)>? _leases;
    private bool _disposed;

    private WindowsMachineRootGuard(
        WindowsMachineDeploymentPlan plan,
        IWindowsInstallerDirectoryNative native,
        bool createMissingProtectedDirectories)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(native);
        plan.Validate();
        _plan = plan;
        _native = native;
        _createMissingProtectedDirectories = createMissingProtectedDirectories;
    }

    internal static WindowsMachineRootGuard CreateDefault(
        WindowsMachineDeploymentPlan plan) =>
        new(
            plan,
            new WindowsInstallerDirectoryNative(),
            createMissingProtectedDirectories: true);

    internal static WindowsMachineRootGuard CreateReadOnlyDefault(
        WindowsMachineDeploymentPlan plan) =>
        new(
            plan,
            new WindowsInstallerDirectoryNative(),
            createMissingProtectedDirectories: false);

    internal static WindowsMachineRootGuard CreateForTesting(
        WindowsMachineDeploymentPlan plan,
        IWindowsInstallerDirectoryNative native) =>
        new(plan, native, createMissingProtectedDirectories: true);

    internal static WindowsMachineRootGuard CreateReadOnlyForTesting(
        WindowsMachineDeploymentPlan plan,
        IWindowsInstallerDirectoryNative native) =>
        new(plan, native, createMissingProtectedDirectories: false);

    public Task EnsureProtectedAsync(
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        RequireExactPlan(plan);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (_leases is null)
                {
                    _leases = Acquire(cancellationToken);
                }
                else
                {
                    Revalidate(_leases, cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (InstallerProtocolException)
            {
                throw;
            }
            catch (Exception exception) when (IsRecoverable(exception))
            {
                throw new InstallerProtocolException(
                    "installer.machine.root_verification_failed",
                    exception);
            }
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            DisposeLeases(_leases);
            _leases = null;
            _disposed = true;
        }
    }

    private List<(DirectorySpec Spec, IWindowsInstallerDirectoryLease Lease)> Acquire(
        CancellationToken cancellationToken)
    {
        var acquired = new List<(DirectorySpec, IWindowsInstallerDirectoryLease)>();
        try
        {
            foreach (DirectorySpec spec in BuildDirectoryChains())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (spec.CreateWithProtectedAcl
                    && _createMissingProtectedDirectories)
                {
                    _native.CreateDirectory(
                        spec.Path,
                        WindowsInstallerDirectorySecurityPolicy
                            .CreateProtectedDirectorySecurity(_plan.Request.TargetSid));
                }

                IWindowsInstallerDirectoryLease lease = _native.OpenDirectory(
                    spec.Path,
                    preventRename: spec.CreateWithProtectedAcl);
                acquired.Add((spec, lease));
                ValidateObservation(spec, lease.Observe());
            }

            Revalidate(acquired, cancellationToken);
            return acquired;
        }
        catch
        {
            DisposeLeases(acquired);
            throw;
        }
    }

    private void Revalidate(
        IReadOnlyList<(DirectorySpec Spec, IWindowsInstallerDirectoryLease Lease)> leases,
        CancellationToken cancellationToken)
    {
        DirectorySpec[] expected = BuildDirectoryChains().ToArray();
        if (leases.Count != expected.Length)
        {
            throw new InstallerProtocolException(
                "installer.machine.root_verification_failed");
        }

        for (int index = 0; index < leases.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (leases[index].Spec != expected[index])
            {
                throw new InstallerProtocolException(
                    "installer.machine.root_verification_failed");
            }

            ValidateObservation(leases[index].Spec, leases[index].Lease.Observe());
        }
    }

    private IEnumerable<DirectorySpec> BuildDirectoryChains()
    {
        foreach (DirectorySpec spec in BuildDirectoryChain(
                     _plan.ProgramFilesRoot,
                     _plan.MachineRoot))
        {
            yield return spec;
        }

        foreach (DirectorySpec spec in BuildDirectoryChain(
                     _plan.CommonApplicationDataRoot,
                     _plan.ServiceDataRoot))
        {
            yield return spec;
        }
    }

    private static IEnumerable<DirectorySpec> BuildDirectoryChain(
        string wellKnownRoot,
        string protectedRoot)
    {
        string volumeRoot = Path.GetPathRoot(wellKnownRoot)
            ?? throw new InstallerProtocolException(
                "installer.machine.root_path_invalid");
        string current = volumeRoot;
        yield return new DirectorySpec(
            current,
            RequiresExactProtection: false,
            CreateWithProtectedAcl: false);
        foreach (string segment in RelativeSegments(volumeRoot, wellKnownRoot))
        {
            current = Path.Combine(current, segment);
            yield return new DirectorySpec(
                current,
                RequiresExactProtection: false,
                CreateWithProtectedAcl: false);
        }

        int protectedIndex = 0;
        current = wellKnownRoot;
        foreach (string segment in RelativeSegments(wellKnownRoot, protectedRoot))
        {
            current = Path.Combine(current, segment);
            yield return new DirectorySpec(
                current,
                RequiresExactProtection: protectedIndex > 0,
                CreateWithProtectedAcl: true);
            protectedIndex++;
        }
    }

    private void ValidateObservation(
        DirectorySpec spec,
        WindowsInstallerDirectoryObservation observation)
    {
        if (!observation.IsDirectory)
        {
            throw new InstallerProtocolException(
                "installer.machine.root_not_directory");
        }

        if (observation.IsReparsePoint)
        {
            throw new InstallerProtocolException(
                "installer.machine.root_reparse_rejected");
        }

        try
        {
            if (spec.RequiresExactProtection)
            {
                WindowsInstallerDirectorySecurityPolicy.ValidateProtectedRoot(
                    observation.Security,
                    _plan.Request.TargetSid);
            }
            else
            {
                WindowsInstallerDirectorySecurityPolicy.ValidateRenameAnchor(
                    observation.Security);
            }
        }
        catch (InstallerProtocolException exception)
        {
            throw new InstallerProtocolException(
                spec.RequiresExactProtection
                    ? "installer.machine.root_acl_invalid"
                    : "installer.machine.root_ancestor_acl_invalid",
                exception);
        }
    }

    private void RequireExactPlan(WindowsMachineDeploymentPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        if (!string.Equals(
                plan.Request.TargetSid,
                _plan.Request.TargetSid,
                StringComparison.Ordinal)
            || !string.Equals(plan.MachineRoot, _plan.MachineRoot, StringComparison.Ordinal)
            || !string.Equals(
                plan.ServiceDataRoot,
                _plan.ServiceDataRoot,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.root_plan_changed");
        }
    }

    private static IEnumerable<string> RelativeSegments(
        string parent,
        string descendant)
    {
        string relative = Path.GetRelativePath(parent, descendant);
        if (Path.IsPathFullyQualified(relative)
            || relative is "." or ".."
            || relative.StartsWith(
                string.Concat("..", Path.DirectorySeparatorChar),
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.machine.root_path_invalid");
        }

        foreach (string segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment is "." or "..")
            {
                throw new InstallerProtocolException(
                    "installer.machine.root_path_invalid");
            }

            yield return segment;
        }
    }

    private static void DisposeLeases(
        IReadOnlyList<(DirectorySpec Spec, IWindowsInstallerDirectoryLease Lease)>? leases)
    {
        if (leases is null)
        {
            return;
        }

        for (int index = leases.Count - 1; index >= 0; index--)
        {
            leases[index].Lease.Dispose();
        }
    }

    private static bool IsRecoverable(Exception exception) =>
        exception is not (OutOfMemoryException
            or StackOverflowException
            or AccessViolationException
            or AppDomainUnloadedException);

    private sealed record DirectorySpec(
        string Path,
        bool RequiresExactProtection,
        bool CreateWithProtectedAcl);
}
