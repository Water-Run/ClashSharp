namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Owns access to one fixed private journal in a pinned SYSTEM/Administrators-only directory.
/// The Windows authority owns this port's lifetime and must keep machine exclusion and directory
/// leases alive until every operation, including post-mutation reconciliation, has completed.
/// </summary>
public interface IInstallerOwnerTransferPersistence
{
    /// <summary>
    /// Revalidates private directory and file security, then returns a bounded owned byte copy.
    /// Null means confirmed file absence under a verified root, never an unreadable or missing root.
    /// The caller clears the returned buffer after use.
    /// </summary>
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Atomically replaces the fixed journal with write-through private temporary bytes, verifying
    /// security before creation. The input memory cannot be retained after the returned task ends.
    /// </summary>
    Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Deletes only the fixed private journal after revalidating its root and file security.</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}
