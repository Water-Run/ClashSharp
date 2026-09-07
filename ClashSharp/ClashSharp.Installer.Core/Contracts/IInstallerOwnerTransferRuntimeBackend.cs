using ClashSharp.Installer.Ownership;

namespace ClashSharp.Installer.Contracts;

/// <summary>Separate explicit account-transfer entry, never inferred from ordinary operation flags.</summary>
public interface IInstallerOwnerTransferRuntimeBackend
{
    /// <summary>Gets whether this composition provides the dedicated authenticated transfer flow.</summary>
    bool SupportsOwnerTransfer { get; }

    /// <summary>Inspects, confirms, transfers and continues the exact protected install or repair.</summary>
    /// <param name="confirm">User decision on the authenticated helper's public offer.</param>
    /// <param name="progress">Best-effort progress from the ordinary continuation.</param>
    /// <param name="cancellationToken">Requests cancellation while all owned work is drained.</param>
    Task<InstallerExecutionResult> TransferAndExecuteAsync(
        Func<InstallerOwnerTransferOffer, CancellationToken, Task<bool>> confirm,
        IProgress<InstallerProgress>? progress, CancellationToken cancellationToken);
}
