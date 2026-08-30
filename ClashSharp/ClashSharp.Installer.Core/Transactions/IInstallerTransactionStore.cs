using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Transactions;

/// <summary>Reads the one machine-authoritative transaction journal without mutation authority.</summary>
public interface IInstallerTransactionReader
{
    /// <summary>Loads the current strict journal, or returns <see langword="null"/> when absent.</summary>
    Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken);
}

/// <summary>Reads and atomically replaces the protected journal inside its elevated authority.</summary>
public interface IInstallerTransactionStore : IInstallerTransactionReader
{

    /// <summary>Saves one generation using the prior content hash as a compare-and-swap token.</summary>
    Task<InstallerTransactionSnapshot> SaveAsync(
        InstallerTransactionJournal journal,
        string? expectedCurrentHash,
        CancellationToken cancellationToken);

    /// <summary>Deletes only an exact verified transaction after a hash check.</summary>
    Task ClearVerifiedAsync(
        string transactionId,
        string expectedCurrentHash,
        CancellationToken cancellationToken);
}

/// <summary>Pairs a validated journal with the SHA-256 of its exact serialized bytes.</summary>
/// <param name="Journal">Validated journal.</param>
/// <param name="ContentHash">Lowercase SHA-256 compare-and-swap token.</param>
public sealed record InstallerTransactionSnapshot(
    InstallerTransactionJournal Journal,
    string ContentHash)
{
    /// <summary>Creates a snapshot whose digest is computed from canonical journal bytes.</summary>
    public static InstallerTransactionSnapshot Create(InstallerTransactionJournal journal)
    {
        ArgumentNullException.ThrowIfNull(journal);
        byte[] bytes = InstallerTransactionCodec.Serialize(journal);
        return new(
            journal,
            Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    /// <summary>Validates the journal and its exact canonical compare-and-swap digest.</summary>
    public void Validate()
    {
        if (Journal is null)
        {
            throw new InstallerProtocolException("installer.transaction.snapshot_invalid");
        }

        Journal.Validate();
        InstallerProtocolValidation.ValidateLowerHex256(
            ContentHash,
            "installer.transaction.content_hash_invalid");
        byte[] bytes = InstallerTransactionCodec.Serialize(Journal);
        string expectedHash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        if (!string.Equals(ContentHash, expectedHash, StringComparison.Ordinal))
        {
            throw new InstallerProtocolException(
                "installer.transaction.content_hash_mismatch");
        }
    }
}
