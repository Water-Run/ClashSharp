using ClashSharp.Installer.Certificates;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Private write-ahead evidence for transferring machine ownership before an ordinary transaction.
/// This document contains both IPC credentials and must never cross the parent/helper IPC boundary
/// or be stored in the user-readable ordinary journal directory.
/// </summary>
/// <param name="Schema">Private transfer protocol version.</param>
/// <param name="Continuation">Exact prepared Install or Repair journal retained across handoff.</param>
/// <param name="PreviousOwner">Previous owner, credential and trusted profile mapping.</param>
/// <param name="NextOwner">Target owner, fresh credential and trusted profile mapping.</param>
/// <param name="PreviousCertificateLedger">Previous owner's certificate evidence to preserve, if any.</param>
/// <param name="NextCertificateLedger">Existing target-owner certificate evidence to restore, if any.</param>
/// <param name="Phase">Last independently verified and durably acknowledged transfer boundary.</param>
/// <param name="Generation">One-based generation uniquely determined by the phase.</param>
public sealed record InstallerOwnerTransferJournal(
    int Schema,
    InstallerTransactionJournal Continuation,
    InstallerOwnerTransferParticipant PreviousOwner,
    InstallerOwnerTransferParticipant NextOwner,
    InstallerCertificateOwnershipLedger? PreviousCertificateLedger,
    InstallerCertificateOwnershipLedger? NextCertificateLedger,
    InstallerOwnerTransferPhase Phase,
    int Generation)
{
    /// <summary>Gets the only supported private owner-transfer schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Creates the initial immutable evidence after separately authorized helper inspection.</summary>
    /// <param name="continuationRequest">Ordinary target-account Install or Repair, with no takeover flag.</param>
    /// <param name="previousOwner">Verified previous owner evidence.</param>
    /// <param name="nextOwner">Verified target evidence with a newly generated credential.</param>
    /// <param name="previousCertificateLedger">Previous certificate ownership that must be retained.</param>
    /// <param name="nextCertificateLedger">Existing target certificate ownership, if previously archived.</param>
    public static InstallerOwnerTransferJournal Create(
        InstallerRequest continuationRequest,
        InstallerOwnerTransferParticipant previousOwner,
        InstallerOwnerTransferParticipant nextOwner,
        InstallerCertificateOwnershipLedger? previousCertificateLedger = null,
        InstallerCertificateOwnershipLedger? nextCertificateLedger = null)
    {
        ArgumentNullException.ThrowIfNull(continuationRequest);
        var journal = new InstallerOwnerTransferJournal(
            CurrentSchema,
            InstallerTransactionJournal.Create(continuationRequest),
            previousOwner,
            nextOwner,
            previousCertificateLedger,
            nextCertificateLedger,
            InstallerOwnerTransferPhase.Prepared,
            1);
        journal.Validate();
        return journal;
    }

    /// <summary>Validates immutable identities and the exact phase/generation relationship.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema)
        {
            throw new InstallerProtocolException("installer.owner_transfer.schema_invalid");
        }

        if (Continuation is null || PreviousOwner is null || NextOwner is null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.identity_missing");
        }

        Continuation.Validate();
        PreviousOwner.Validate();
        NextOwner.Validate();
        if (Continuation.Operation is not (InstallerOperation.Install or InstallerOperation.Repair)
            || Continuation.AllowReassociation
            || Continuation.Phase != InstallerTransactionPhase.Prepared
            || Continuation.Generation != 1)
        {
            throw new InstallerProtocolException("installer.owner_transfer.continuation_invalid");
        }

        if (!string.Equals(Continuation.TargetSid, NextOwner.Association.OwnerSid, StringComparison.Ordinal)
            || string.Equals(PreviousOwner.Association.OwnerSid, NextOwner.Association.OwnerSid, StringComparison.Ordinal)
            || string.Equals(PreviousOwner.ProfileRoot, NextOwner.ProfileRoot, StringComparison.OrdinalIgnoreCase)
            || string.Equals(
                PreviousOwner.Association.AuthenticationToken,
                NextOwner.Association.AuthenticationToken,
                StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.owner_transfer.identity_conflict");
        }

        ValidateCertificateLedger(PreviousCertificateLedger, PreviousOwner.Association.OwnerSid);
        ValidateCertificateLedger(NextCertificateLedger, NextOwner.Association.OwnerSid);
        if (!Enum.IsDefined(Phase))
        {
            throw new InstallerProtocolException("installer.owner_transfer.phase_invalid");
        }

        if (Generation != (int)Phase + 1)
        {
            throw new InstallerProtocolException("installer.owner_transfer.generation_invalid");
        }
    }

    /// <summary>Advances exactly one acknowledged boundary or accepts an unchanged replay.</summary>
    /// <param name="next">Same phase or its immediate successor.</param>
    public InstallerOwnerTransferJournal TransitionTo(InstallerOwnerTransferPhase next)
    {
        Validate();
        if (next == Phase)
        {
            return this;
        }

        if (!Enum.IsDefined(next) || (int)next != (int)Phase + 1)
        {
            throw new InstallerProtocolException("installer.owner_transfer.phase_transition_invalid");
        }

        InstallerOwnerTransferJournal advanced = this with { Phase = next, Generation = Generation + 1 };
        advanced.Validate();
        return advanced;
    }

    /// <summary>Compares every immutable field independently of durable progress.</summary>
    /// <param name="other">Another validated private transfer document.</param>
    public bool HasSameIdentity(InstallerOwnerTransferJournal other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Validate();
        other.Validate();
        return this with { Phase = other.Phase, Generation = other.Generation } == other;
    }

    /// <summary>Returns progress only, excluding account, profile, credential and certificate evidence.</summary>
    public override string ToString() => $"InstallerOwnerTransferJournal {{ Phase = {Phase}, Generation = {Generation} }}";

    private static void ValidateCertificateLedger(InstallerCertificateOwnershipLedger? ledger, string ownerSid)
    {
        if (ledger is null)
        {
            return;
        }

        ledger.Validate();
        if (!string.Equals(ledger.TargetSid, ownerSid, StringComparison.Ordinal)
            || ledger.ManagedReferenceCount != 1)
        {
            throw new InstallerProtocolException("installer.owner_transfer.certificate_identity_invalid");
        }
    }
}
