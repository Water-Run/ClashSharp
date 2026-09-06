using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>Pairs private transfer evidence with its canonical compare-and-swap digest.</summary>
/// <param name="Journal">Private validated owner-transfer journal.</param>
/// <param name="ContentHash">Lowercase SHA-256 of the complete canonical document.</param>
public sealed record InstallerOwnerTransferSnapshot(
    InstallerOwnerTransferJournal Journal,
    string ContentHash)
{
    /// <summary>Computes a snapshot without retaining serialized credential buffers.</summary>
    /// <param name="journal">Journal whose canonical bytes define the snapshot.</param>
    public static InstallerOwnerTransferSnapshot Create(InstallerOwnerTransferJournal journal) =>
        new(journal, ComputeHash(journal));

    /// <summary>Checks journal semantics and the digest of every immutable field and progress value.</summary>
    public void Validate()
    {
        if (Journal is null)
        {
            throw new InstallerProtocolException("installer.owner_transfer.snapshot_invalid");
        }

        Journal.Validate();
        InstallerProtocolValidation.ValidateLowerHex256(
            ContentHash,
            "installer.owner_transfer.content_hash_invalid");
        if (!string.Equals(ContentHash, ComputeHash(Journal), StringComparison.Ordinal))
        {
            throw new InstallerProtocolException("installer.owner_transfer.content_hash_mismatch");
        }
    }

    /// <summary>Returns progress without disclosing nested private evidence.</summary>
    public override string ToString() => $"InstallerOwnerTransferSnapshot {{ {Journal} }}";

    private static string ComputeHash(InstallerOwnerTransferJournal journal)
    {
        byte[] bytes = InstallerOwnerTransferCodec.Serialize(journal);
        try
        {
            return Convert.ToHexStringLower(SHA256.HashData(bytes));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
