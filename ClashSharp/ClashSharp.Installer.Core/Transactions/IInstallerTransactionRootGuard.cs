namespace ClashSharp.Installer.Transactions;

/// <summary>
/// Proves that the fixed journal root rejects unprivileged mutation and that its ancestors reject
/// unprivileged delete, rename, or ACL takeover of that root.
/// </summary>
public interface IInstallerTransactionRootGuard
{
    /// <summary>
    /// Verifies root/ancestor owners, ACL replacement rights, and reparse policy. Implementations
    /// must fail closed; a shared anchor may retain create-only grants that cannot replace the root.
    /// </summary>
    Task EnsureProtectedAsync(string absoluteRootPath, CancellationToken cancellationToken);
}
