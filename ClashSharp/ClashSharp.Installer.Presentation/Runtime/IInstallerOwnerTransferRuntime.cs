using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Runtime;

/// <summary>Public consent presentation; account identities, paths and credentials stay in the backend.</summary>
/// <param name="IsRecovery">Whether an already confirmed unfinished transfer is being resumed.</param>
public sealed record InstallerOwnerTransferConfirmation(bool IsRecovery);

/// <summary>Optional separately confirmed transfer action for the single-product shell.</summary>
public interface IInstallerOwnerTransferRuntime
{
    /// <summary>Gets whether the trusted runtime supplies this dedicated action.</summary>
    bool SupportsOwnerTransfer { get; }

    /// <summary>Awaits explicit consent and continues installation through the same owned session.</summary>
    Task<InstallerExecutionResult> TransferAndExecuteAsync(
        Func<InstallerOwnerTransferConfirmation, CancellationToken, Task<bool>> confirm,
        IProgress<InstallerProgress> progress, CancellationToken cancellationToken);
}
