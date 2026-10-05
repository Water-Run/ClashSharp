namespace ClashSharp.Installer.Transactions;

/// <summary>Provides exact-hash cleanup authority for the original-restored terminal alone.</summary>
public interface IInstallerOriginalRestorationStore
{
    /// <summary>Deletes the exact independently verified original-restored repair journal.</summary>
    Task ClearOriginalRestoredAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken);
}
