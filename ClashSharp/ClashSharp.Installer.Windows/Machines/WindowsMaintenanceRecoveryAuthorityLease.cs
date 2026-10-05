using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Borrows the authenticated helper's machine/App exclusion and owns both original file leases.
/// The outer helper waits for all SCM and terminal reconciliation before disposing these pins and
/// then releasing exclusion. This capability cannot acquire a different account's authority.
/// </summary>
internal sealed class WindowsMaintenanceRecoveryAuthorityLease : IWindowsMaintenanceRecoveryAuthorityLease, IDisposable
{
    private readonly IWindowsInstallerAuthorityLease _machine;
    private readonly IWindowsInstallerApplicationLease _application;
    private readonly WindowsMaintenancePackageStateLease _package;
    private readonly WindowsMaintenancePayloadStateLease _payload;
    private readonly string _targetSid;
    private bool _disposed;

    private WindowsMaintenanceRecoveryAuthorityLease(IWindowsInstallerAuthorityLease machine,
        IWindowsInstallerApplicationLease application, WindowsMaintenancePackageStateLease package,
        WindowsMaintenancePayloadStateLease payload, string targetSid)
    {
        _machine = machine;
        _application = application;
        _package = package;
        _payload = payload;
        _targetSid = targetSid;
    }

    internal string PackageContentsSha256 => _package.ContentsSha256;
    internal IReadOnlyList<WindowsMaintenanceFileFingerprint> PayloadFingerprints => _payload.Fingerprints;

    internal static async Task<WindowsMaintenanceRecoveryAuthorityLease> AcquireAsync(WindowsMachineDeploymentPlan plan,
        string packageRoot, IWindowsInstallerAuthorityLease machine, IWindowsInstallerApplicationLease application,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(application);
        plan.Validate();
        if (plan.Request.Operation != InstallerOperation.Repair || application.TargetSid != plan.Request.TargetSid)
        {
            throw new InstallerProtocolException("installer.recovery.authority_mismatch");
        }
        await machine.ReverifyAsync(cancellationToken).ConfigureAwait(false);
        application.Reverify(cancellationToken);
        WindowsMaintenancePackageStateLease? package = null;
        WindowsMaintenancePayloadStateLease? payload = null;
        bool returned = false;
        try
        {
            package = WindowsMaintenancePackageStateLease.Acquire(packageRoot, cancellationToken);
            payload = new WindowsMaintenancePayloadStateReader().Acquire(plan, packageRoot, cancellationToken);
            var lease = new WindowsMaintenanceRecoveryAuthorityLease(machine, application, package, payload, plan.Request.TargetSid);
            await lease.ReverifyAsync(cancellationToken).ConfigureAwait(false);
            returned = true;
            return lease;
        }
        finally
        {
            if (!returned) { payload?.Dispose(); package?.Dispose(); }
        }
    }

    public async Task ReverifyAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_application.TargetSid != _targetSid) { throw new InstallerProtocolException("installer.recovery.authority_mismatch"); }
        await _machine.ReverifyAsync(cancellationToken).ConfigureAwait(false);
        _application.Reverify(cancellationToken);
        _package.Reverify(cancellationToken);
        _payload.Reverify(cancellationToken);
        _application.Reverify(cancellationToken);
        await _machine.ReverifyAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        try { _payload.Dispose(); }
        finally { _package.Dispose(); }
        // Borrowed outer authority deliberately remains owned by the authenticated helper.
    }
}
