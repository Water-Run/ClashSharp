using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Validates private transfer persistence transitions. Callers must hold machine-wide authority
/// across read/compare/write and independently verify each native postcondition before advancing.
/// </summary>
public static class InstallerOwnerTransferStatePolicy
{
    /// <summary>Accepts creation, an exact replay, or one immutable-identity-preserving advance.</summary>
    /// <param name="incoming">Validated journal proposed for persistence.</param>
    /// <param name="current">Existing canonical snapshot, or null only after confirmed absence.</param>
    /// <param name="expectedCurrentHash">Exact prior digest, or null only for initial creation.</param>
    public static void ValidateSave(
        InstallerOwnerTransferJournal incoming,
        InstallerOwnerTransferSnapshot? current,
        string? expectedCurrentHash)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        incoming.Validate();
        if (current is null)
        {
            if (expectedCurrentHash is not null || incoming.Phase != InstallerOwnerTransferPhase.Prepared)
            {
                throw new InstallerProtocolException("installer.owner_transfer.write_conflict");
            }

            return;
        }

        current.Validate();
        if (!string.Equals(expectedCurrentHash, current.ContentHash, StringComparison.Ordinal)
            || !current.Journal.HasSameIdentity(incoming))
        {
            throw new InstallerProtocolException("installer.owner_transfer.write_conflict");
        }

        _ = current.Journal.TransitionTo(incoming.Phase);
    }

    /// <summary>Accepts deletion only after the final boundary for the exact transaction and digest.</summary>
    /// <param name="current">Current validated private evidence, never inferred from a caller's claim.</param>
    /// <param name="transactionId">ID of the still-retained ordinary continuation.</param>
    /// <param name="expectedCurrentHash">Exact digest of the verified private journal.</param>
    public static void ValidateClear(
        InstallerOwnerTransferSnapshot? current,
        string transactionId,
        string expectedCurrentHash)
    {
        InstallerProtocolValidation.ValidateLowerHex256(
            transactionId,
            "installer.owner_transfer.transaction_id_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            expectedCurrentHash,
            "installer.owner_transfer.content_hash_invalid");
        current?.Validate();
        if (current is null
            || current.Journal.Phase != InstallerOwnerTransferPhase.Verified
            || !string.Equals(current.Journal.Continuation.TransactionId, transactionId, StringComparison.Ordinal)
            || !string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.owner_transfer.clear_conflict");
        }
    }
}
