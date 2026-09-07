using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Observes three independently protected ledger slots. A live reference can move between slots
/// only after its exact bytes are preserved in the destination; its account and generation do not change.
/// </summary>
/// <param name="Active">The ordinary Installer/v2 certificate ledger, if present.</param>
/// <param name="PreviousArchive">Private retained evidence for the previous account, if present.</param>
/// <param name="NextArchive">Private evidence awaiting activation for the next account, if present.</param>
public sealed record InstallerOwnerTransferCertificateState(
    InstallerCertificateOwnershipLedger? Active,
    InstallerCertificateOwnershipLedger? PreviousArchive,
    InstallerCertificateOwnershipLedger? NextArchive)
{
    /// <summary>
    /// Selects the single next mutation from an exact reachable state, or returns null when complete.
    /// This policy never grants filesystem authority or changes a certificate store.
    /// </summary>
    /// <param name="journal">Durable private evidence at AssociationTransferred.</param>
    public InstallerOwnerTransferCertificateStep? GetNextStep(InstallerOwnerTransferJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        if (journal.Phase != InstallerOwnerTransferPhase.AssociationTransferred)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_phase_invalid");
        }

        InstallerCertificateOwnershipLedger? previous = journal.PreviousCertificateLedger;
        InstallerCertificateOwnershipLedger? next = journal.NextCertificateLedger;
        if ((Active != previous && Active != next)
            || (PreviousArchive is not null && PreviousArchive != previous)
            || (NextArchive is not null && NextArchive != next))
        {
            throw Conflict();
        }

        // The all-null case is already complete. For different accounts, non-null ledgers cannot
        // be equal, so every other state has an unambiguous before/after activation boundary.
        if (Active == next)
        {
            if (PreviousArchive != previous)
            {
                // Activation cannot have preceded durable preservation of the previous ledger.
                throw Conflict();
            }
            return NextArchive is null ? null : new(
                InstallerOwnerTransferCertificateAction.ReleaseActivatedArchive,
                this with { NextArchive = null });
        }

        if (NextArchive != next)
        {
            // A target ledger cannot disappear before its exact active copy has been observed.
            throw Conflict();
        }
        if (PreviousArchive != previous)
        {
            return new(InstallerOwnerTransferCertificateAction.PreservePrevious,
                this with { PreviousArchive = previous });
        }
        return new(InstallerOwnerTransferCertificateAction.ActivateNext, this with { Active = next });
    }

    /// <summary>Omits account and certificate evidence from diagnostic descriptions.</summary>
    public override string ToString() => "InstallerOwnerTransferCertificateState { Private certificate evidence }";

    private static InstallerProtocolException Conflict() =>
        new("installer.owner_transfer.certificate_state_conflict");
}
