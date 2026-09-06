namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Applies native transfer steps inside authenticated, machine-exclusive authority with both App
/// lifetime barriers and all required directory leases held. Private evidence never crosses IPC.
/// </summary>
public interface IInstallerOwnerTransferPhaseExecutor
{
    /// <summary>
    /// Applies and independently verifies the immediate successor from StartupBlocked through
    /// InstallerAccessTransferred. Replays must accept exact already-completed postconditions;
    /// unknown state must fail without replacing evidence. A successful task means the complete
    /// step postcondition holds, even when the operation was already applied before interruption.
    /// </summary>
    /// <param name="current">Exact durable private evidence before the step.</param>
    /// <param name="nextPhase">Immediate mutation boundary to apply and verify.</param>
    /// <param name="cancellationToken">Cancels owned work without abandoning mutations in progress.</param>
    Task ApplyAndVerifyAsync(
        InstallerOwnerTransferJournal current,
        InstallerOwnerTransferPhase nextPhase,
        CancellationToken cancellationToken);

    /// <summary>
    /// Observes all transfer postconditions without mutation. In particular, every recorded
    /// certificate ownership ledger must remain preserved and the exact ordinary Prepared continuation must
    /// still block App startup. This check also runs when resuming an already Verified journal.
    /// </summary>
    /// <param name="current">Durable InstallerAccessTransferred or Verified evidence.</param>
    /// <param name="cancellationToken">Cancels before or during the owned verification.</param>
    Task VerifyCompletedAsync(
        InstallerOwnerTransferJournal current,
        CancellationToken cancellationToken);
}
