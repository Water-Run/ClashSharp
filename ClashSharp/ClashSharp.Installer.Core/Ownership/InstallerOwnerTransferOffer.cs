using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>Displays the exact account transition without exporting private journal credentials.</summary>
/// <param name="Request">Candidate and dedicated session bound to this offer.</param>
/// <param name="OfferId">Fresh helper-generated confirmation nonce.</param>
/// <param name="PreviousOwnerSid">Account whose machine association will be replaced.</param>
/// <param name="NextOwnerSid">Authenticated account receiving the association.</param>
/// <param name="IsRecovery">Whether the helper found an existing private transfer.</param>
public sealed record InstallerOwnerTransferOffer(InstallerOwnerTransferRequest Request, string OfferId,
    string PreviousOwnerSid, string NextOwnerSid, bool IsRecovery)
{
    /// <summary>Validates the public identity and two distinct account participants.</summary>
    public void Validate()
    {
        ArgumentNullException.ThrowIfNull(Request);
        Request.Validate();
        InstallerProtocolValidation.ValidateLowerHex256(OfferId, "installer.owner_transfer.offer_invalid");
        InstallerProtocolValidation.ValidateTargetSid(PreviousOwnerSid);
        InstallerProtocolValidation.ValidateTargetSid(NextOwnerSid);
        if (PreviousOwnerSid == NextOwnerSid)
        {
            throw new InstallerProtocolException("installer.owner_transfer.identity_conflict");
        }
    }

    /// <summary>Requires the offered candidate and target to match the parent's own request.</summary>
    public void ValidateAgainst(InstallerOwnerTransferRequest request, string authenticatedTargetSid)
    {
        Validate();
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (Request != (IsRecovery ? request with { Operation = Request.Operation } : request)
            || NextOwnerSid != authenticatedTargetSid)
        {
            throw new InstallerProtocolException("installer.owner_transfer.offer_mismatch");
        }
    }

    /// <summary>Omits account identities from routine diagnostic formatting.</summary>
    public override string ToString() => $"InstallerOwnerTransferOffer {{ IsRecovery = {IsRecovery} }}";
}

/// <summary>One explicit decision bound to both session and helper-generated offer.</summary>
/// <param name="SessionId">Dedicated session nonce.</param>
/// <param name="OfferId">Exact displayed offer nonce.</param>
/// <param name="Accepted">Whether the user explicitly accepted the displayed transition.</param>
public sealed record InstallerOwnerTransferDecision(string SessionId, string OfferId, bool Accepted)
{
    /// <summary>Rejects decisions from any other session or inspection.</summary>
    public void ValidateAgainst(InstallerOwnerTransferOffer offer)
    {
        ArgumentNullException.ThrowIfNull(offer);
        offer.Validate();
        if (SessionId != offer.Request.SessionId || OfferId != offer.OfferId)
        {
            throw new InstallerProtocolException("installer.owner_transfer.decision_mismatch");
        }
    }
}
