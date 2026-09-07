using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>Derives only fixed active/private ledger leaves from durable, validated participants.</summary>
internal sealed record WindowsOwnerTransferCertificatePlan(
    WindowsMachineDeploymentRoots Roots, InstallerOwnerTransferJournal Journal)
{
    internal string PrivateRoot => Path.Combine(Roots.CommonApplicationDataRoot,
        InstallerStateLayout.ProductDirectoryName, InstallerOwnerTransferStateLayout.AuthorityDirectoryName,
        InstallerOwnerTransferStateLayout.VersionDirectoryName);

    internal string ActivePath => Path.Combine(Roots.CommonApplicationDataRoot,
        InstallerStateLayout.ProductDirectoryName, InstallerStateLayout.InstallerDirectoryName,
        InstallerStateLayout.VersionDirectoryName, FileInstallerCertificateOwnershipStore.LedgerFileName);

    internal string PreviousArchivePath => GetArchivePath(Journal.PreviousOwner.Association.OwnerSid);
    internal string NextArchivePath => GetArchivePath(Journal.NextOwner.Association.OwnerSid);

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Roots);
        ArgumentNullException.ThrowIfNull(Journal);
        Roots.Validate();
        Journal.Validate();
        WindowsOwnerTransferAccessPolicy.ValidateParticipants(
            Journal.PreviousOwner.Association.OwnerSid, Journal.NextOwner.Association.OwnerSid);
        if (Journal.Phase != InstallerOwnerTransferPhase.AssociationTransferred)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_phase_invalid");
        }
    }

    public override string ToString() => "WindowsOwnerTransferCertificatePlan { Private certificate files }";

    private string GetArchivePath(string sid) => Path.Combine(PrivateRoot,
        $"certificate-owner-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))}.json");
}

/// <summary>Requires pinned active/private parents and continuously held exclusive helper authority.</summary>
internal interface IWindowsOwnerTransferCertificateFileNative
{
    Task<InstallerOwnerTransferCertificateState> ReadAsync(
        WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken);

    Task ApplyAsync(WindowsOwnerTransferCertificatePlan plan, InstallerOwnerTransferCertificateState expected,
        InstallerOwnerTransferCertificateStep step, CancellationToken cancellationToken);
}

internal interface IWindowsOwnerTransferCertificateBoundary : IWindowsOwnerTransferAssociationBoundary
{
    void VerifyAssociation(ClashSharp.Installer.Machines.InstallerMachineAssociation expected);
}
