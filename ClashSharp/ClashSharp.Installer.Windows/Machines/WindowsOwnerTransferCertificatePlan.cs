using System.Security.Cryptography;
using System.Text;
using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Windows.FileSecurity;

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
        if (Journal.Phase is not (InstallerOwnerTransferPhase.Prepared or InstallerOwnerTransferPhase.AssociationTransferred
            or InstallerOwnerTransferPhase.CertificateStateTransferred
            or InstallerOwnerTransferPhase.InstallerAccessTransferred or InstallerOwnerTransferPhase.Verified))
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_phase_invalid");
        }
    }

    /// <summary>Reads the active leaf across the one durable phase in which its DACL can change.</summary>
    internal bool HasActiveFileAccess(WindowsDirectorySecuritySnapshot security) =>
        (Journal.Phase is (InstallerOwnerTransferPhase.Prepared or InstallerOwnerTransferPhase.AssociationTransferred
                or InstallerOwnerTransferPhase.CertificateStateTransferred)
            && WindowsOwnerTransferAccessPolicy.HasOwnerAccess(security, Journal.PreviousOwner.Association.OwnerSid,
                directory: false, inherited: true))
        || (Journal.Phase is (InstallerOwnerTransferPhase.CertificateStateTransferred or InstallerOwnerTransferPhase.InstallerAccessTransferred
                or InstallerOwnerTransferPhase.Verified)
            && WindowsOwnerTransferAccessPolicy.HasOwnerAccess(security, Journal.NextOwner.Association.OwnerSid,
                directory: false, inherited: true));

    /// <summary>Requires the activated ledger and preserved old account evidence after file transfer.</summary>
    internal void VerifyCompletedState(InstallerOwnerTransferCertificateState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (Journal.Phase is not (InstallerOwnerTransferPhase.CertificateStateTransferred
            or InstallerOwnerTransferPhase.InstallerAccessTransferred or InstallerOwnerTransferPhase.Verified))
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_phase_invalid");
        }
        if (state != new InstallerOwnerTransferCertificateState(Journal.NextCertificateLedger, Journal.PreviousCertificateLedger, null))
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_postcondition_failed");
        }
    }

    public override string ToString() => "WindowsOwnerTransferCertificatePlan { Private certificate files }";

    private string GetArchivePath(string sid) => Path.Combine(PrivateRoot,
        $"certificate-owner-{Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(sid)))}.json");
}

/// <summary>Requires pinned active/private parents and continuously held exclusive helper authority.</summary>
internal interface IWindowsOwnerTransferCertificateStateReader
{
    Task<InstallerOwnerTransferCertificateState> ReadAsync(
        WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken);
}

/// <summary>Mutation remains confined to the AssociationTransferred phase and its exact state policy.</summary>
internal interface IWindowsOwnerTransferCertificateFileNative : IWindowsOwnerTransferCertificateStateReader
{
    Task ApplyAsync(WindowsOwnerTransferCertificatePlan plan, InstallerOwnerTransferCertificateState expected,
        InstallerOwnerTransferCertificateStep step, CancellationToken cancellationToken);
}

internal interface IWindowsOwnerTransferCertificateBoundary : IWindowsOwnerTransferAssociationBoundary
{
    void VerifyAssociation(ClashSharp.Installer.Machines.InstallerMachineAssociation expected);
}
