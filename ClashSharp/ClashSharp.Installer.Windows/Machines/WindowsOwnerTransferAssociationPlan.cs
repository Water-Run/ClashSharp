using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Machines;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Binds the only replaceable leaf and its recovery temporary to durable private transaction
/// identity. The caller supplies independently verified fixed roots inside machine authority.
/// </summary>
internal sealed record WindowsOwnerTransferAssociationPlan(
    WindowsMachineDeploymentRoots Roots, string TransactionId,
    InstallerMachineAssociation Previous, InstallerMachineAssociation Next)
{
    internal string AssociationPath => Path.Combine(Roots.ServiceDataRoot, "association.json");
    internal string TemporaryPath => Path.Combine(Roots.ServiceDataRoot, $".association-transfer-{TransactionId}.tmp");

    internal void Validate()
    {
        ArgumentNullException.ThrowIfNull(Roots);
        ArgumentNullException.ThrowIfNull(Previous);
        ArgumentNullException.ThrowIfNull(Next);
        Roots.Validate();
        Previous.Validate();
        Next.Validate();
        InstallerProtocolValidation.ValidateLowerHex256(TransactionId, "installer.owner_transfer.association_transaction_invalid");
        WindowsOwnerTransferAccessPolicy.ValidateParticipants(Previous.OwnerSid, Next.OwnerSid);
        if (Previous.AuthenticationToken == Next.AuthenticationToken)
        {
            throw new InstallerProtocolException("installer.owner_transfer.association_identity_invalid");
        }
    }

    public override string ToString() => "WindowsOwnerTransferAssociationPlan { Private transfer files }";
}

internal sealed record WindowsOwnerTransferAssociationObservation(
    InstallerMachineAssociation Association, bool TemporaryPresent);

internal interface IWindowsOwnerTransferAssociationFileNative
{
    Task<WindowsOwnerTransferAssociationObservation> InspectAsync(
        WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken);

    Task ReplaceExactAsync(WindowsOwnerTransferAssociationPlan plan, CancellationToken cancellationToken);
}

/// <summary>
/// Pins every preserved object and requires completed shared ACLs. Association and the one
/// transaction-bound temporary are checked by the replacement port instead of held against rename.
/// </summary>
internal interface IWindowsOwnerTransferAssociationBoundary : IDisposable
{
    void Reverify(CancellationToken cancellationToken);

    void VerifyContinuation(ClashSharp.Installer.Transactions.InstallerTransactionJournal expected);
}
