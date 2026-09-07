using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Ownership;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Checks the confirmed new-transfer state before private Prepared is saved. The authority has
/// already established the private root and holds both App locks; no ordinary barrier is published here.
/// </summary>
internal sealed class WindowsOwnerTransferPreparationVerifier
{
    private readonly IWindowsOwnerTransferAccessNative _directories;
    private readonly IWindowsOwnerTransferCertificateStateReader _certificates;
    private readonly IInstallerTransactionReader _ordinary;

    internal WindowsOwnerTransferPreparationVerifier(IWindowsOwnerTransferAccessNative directories,
        IWindowsOwnerTransferCertificateStateReader certificates, IInstallerTransactionReader ordinary)
    {
        ArgumentNullException.ThrowIfNull(directories);
        ArgumentNullException.ThrowIfNull(certificates);
        ArgumentNullException.ThrowIfNull(ordinary);
        _directories = directories;
        _certificates = certificates;
        _ordinary = ordinary;
    }

    internal async Task VerifyAsync(WindowsOwnerTransferCertificatePlan plan, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        plan.Validate();
        if (plan.Journal.Phase != InstallerOwnerTransferPhase.Prepared)
        {
            throw new InstallerProtocolException("installer.owner_transfer.preparation_phase_invalid");
        }
        using WindowsOwnerTransferAccessTree tree = WindowsOwnerTransferAccessTree.AcquireForStartupBlock(
            plan.Roots, plan.Journal, _directories, cancellationToken);
        if (await _ordinary.LoadAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.ordinary_state_pending");
        }
        InstallerOwnerTransferCertificateState state = await _certificates.ReadAsync(plan, cancellationToken).ConfigureAwait(false);
        if (state != new InstallerOwnerTransferCertificateState(
            plan.Journal.PreviousCertificateLedger, null, plan.Journal.NextCertificateLedger))
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_state_conflict");
        }
        tree.VerifyAssociation(plan.Journal.PreviousOwner.Association);
        tree.Reverify(requireTransferred: true, cancellationToken);
    }
}
