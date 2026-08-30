namespace ClashSharp.Installer.Certificates;

/// <summary>Provides compare-and-swap persistence for one target user's certificate ownership.</summary>
public interface IInstallerCertificateOwnershipStore
{
    /// <summary>Loads strict current ownership state, or returns <see langword="null"/> when absent.</summary>
    Task<InstallerCertificateOwnershipSnapshot?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Creates or advances the ledger after checking the exact prior content hash.</summary>
    Task<InstallerCertificateOwnershipSnapshot> SaveAsync(
        InstallerCertificateOwnershipLedger ledger,
        string? expectedCurrentHash,
        CancellationToken cancellationToken);

    /// <summary>Clears only an exact ledger whose managed reference count is already zero.</summary>
    Task ClearUnreferencedAsync(
        string ledgerId,
        string expectedCurrentHash,
        CancellationToken cancellationToken);
}

/// <summary>Couples one validated ledger with the SHA-256 of its canonical persisted bytes.</summary>
public sealed record InstallerCertificateOwnershipSnapshot(
    InstallerCertificateOwnershipLedger Ledger,
    string ContentHash);
