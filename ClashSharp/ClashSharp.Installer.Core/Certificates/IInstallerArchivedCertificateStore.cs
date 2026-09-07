namespace ClashSharp.Installer.Certificates;

/// <summary>
/// Reads and releases one retired account's private certificate archive. It cannot create a
/// ledger, import a certificate, acquire ownership or increase a managed reference.
/// </summary>
public interface IInstallerArchivedCertificateStore
{
    /// <summary>Loads the exact account-bound canonical archive, or returns null when absent.</summary>
    Task<InstallerCertificateOwnershipSnapshot?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Releases the existing reference after comparing the complete expected snapshot.</summary>
    Task<InstallerCertificateOwnershipSnapshot> ReleaseReferenceAsync(
        InstallerCertificateOwnershipSnapshot expected, CancellationToken cancellationToken);

    /// <summary>Clears only the exact unreferenced archive after its caller verifies removal.</summary>
    Task ClearAsync(InstallerCertificateOwnershipSnapshot expected, CancellationToken cancellationToken);
}

/// <summary>
/// Bounded private archive I/O. The helper owns the fixed account path, pinned private roots and
/// machine-wide exclusion throughout every call, including reconciliation without cancellation.
/// </summary>
public interface IInstallerArchivedCertificatePersistence
{
    /// <summary>Reads owned bytes from the fixed archive, or returns null for proven absence.</summary>
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Atomically replaces the existing archive within the pinned private directory.</summary>
    Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Deletes the fixed archive after its caller has compared the exact prior state.</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}
