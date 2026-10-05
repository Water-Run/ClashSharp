using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;
using ClashSharp.Installer.Windows.Execution;

namespace ClashSharp.Installer.Windows.Machines;

internal interface IWindowsMachineHelperRecoveryResourcesFactory : IWindowsMachineHelperAuthorityResourcesFactory
{
    IWindowsMachineHelperAuthorityResources Create(string targetSid, IWindowsInstallerAuthorityLease machine,
        IWindowsInstallerApplicationLease application);
}

internal sealed record WindowsMachineHelperRecoveryAuthority(IWindowsInstallerAuthorityLease Machine,
    IWindowsInstallerApplicationLease Application, IInstallerTransactionReader Transactions);

/// <summary>Builds recovery resources only after the helper authenticates and retains both outer leases.</summary>
internal sealed class WindowsMachineHelperRecoveryResourcesFactory : IWindowsMachineHelperRecoveryResourcesFactory
{
    private readonly byte[] _manifest;

    internal WindowsMachineHelperRecoveryResourcesFactory(ReadOnlyMemory<byte> manifest)
    {
        _manifest = manifest.ToArray();
    }

    public IWindowsMachineHelperAuthorityResources Create(string targetSid) =>
        throw new InstallerProtocolException("installer.recovery.authority_required");

    public IWindowsMachineHelperAuthorityResources Create(string targetSid, IWindowsInstallerAuthorityLease machine,
        IWindowsInstallerApplicationLease application)
    {
        ArgumentNullException.ThrowIfNull(machine);
        ArgumentNullException.ThrowIfNull(application);
        if (targetSid != application.TargetSid) { throw new InstallerProtocolException("installer.recovery.authority_mismatch"); }
        return WindowsMachineHelperAuthorityResources.CreateDefault(targetSid, (certificates, transactions) =>
            WindowsMachineHelperOperationExecutor.CreateWithRecovery(_manifest, certificates,
                new WindowsMachineHelperRecoveryAuthority(machine, application, transactions)));
    }
}
