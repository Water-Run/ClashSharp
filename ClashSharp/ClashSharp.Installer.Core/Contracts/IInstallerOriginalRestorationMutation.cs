using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Contracts;

/// <summary>Restores a pending repair's original installation through independently verified elevated authority.</summary>
public interface IInstallerOriginalRestorationMutation
{
    /// <summary>Restores the original installation or verifies an existing original-restored terminal.</summary>
    Task<InstallerTransactionSnapshot> RestoreOriginalAsync(InstallerRequest request, IInstallerReleaseLease release,
        InstallerTransactionSnapshot durableState, CancellationToken cancellationToken);

    /// <summary>Reverifies preservation, clears the exact restoration terminal, and returns its immutable receipt.</summary>
    Task<InstallerTransactionSnapshot> ClearOriginalRestoredAsync(InstallerRequest request, IInstallerReleaseLease release,
        InstallerTransactionSnapshot restoredState, CancellationToken cancellationToken);
}
