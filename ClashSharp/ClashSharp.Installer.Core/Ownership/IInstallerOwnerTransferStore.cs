namespace ClashSharp.Installer.Ownership;

/// <summary>Persists private owner-transfer state within an already-exclusive elevated authority.</summary>
public interface IInstallerOwnerTransferStore
{
    /// <summary>Reads the canonical journal or confirmed absence, without modifying evidence.</summary>
    Task<InstallerOwnerTransferSnapshot?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Creates or advances exact evidence, reconciling a possibly lost write acknowledgement.</summary>
    Task<InstallerOwnerTransferSnapshot> SaveAsync(
        InstallerOwnerTransferJournal journal,
        string? expectedCurrentHash,
        CancellationToken cancellationToken);

    /// <summary>Clears only an exact verified transfer while its ordinary continuation remains owned.</summary>
    Task ClearVerifiedAsync(
        string transactionId,
        string expectedCurrentHash,
        CancellationToken cancellationToken);
}
