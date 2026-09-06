using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Resumes exact persisted private evidence, acknowledges each independently verified native step,
/// and hands off its still-pending ordinary continuation. The caller owns authorization and every
/// authority/resource lease until this operation has completed, failed or drained cancellation.
/// </summary>
public sealed class InstallerOwnerTransferCoordinator
{
    private readonly IInstallerOwnerTransferStore _store;
    private readonly IInstallerOwnerTransferPhaseExecutor _executor;
    private int _active;

    /// <summary>Initializes pure orchestration without acquiring authority or touching state.</summary>
    /// <param name="store">Private durable evidence within the caller's exclusive authority.</param>
    /// <param name="executor">Native operations independently bound to the authenticated candidate.</param>
    public InstallerOwnerTransferCoordinator(
        IInstallerOwnerTransferStore store,
        IInstallerOwnerTransferPhaseExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(executor);
        _store = store;
        _executor = executor;
    }

    /// <summary>
    /// Resumes a previously saved Prepared or later snapshot. Refuses missing or changed evidence
    /// before native work; cancellation or failure preserves the last acknowledged phase for replay.
    /// </summary>
    /// <param name="expected">Exact snapshot inspected inside the same authorized helper session.</param>
    /// <param name="cancellationToken">Cancels owned work, preserving pending recovery evidence.</param>
    /// <returns>The ordinary Prepared continuation after verified private-state removal.</returns>
    public async Task<InstallerTransactionSnapshot> ResumeAsync(
        InstallerOwnerTransferSnapshot expected,
        CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InstallerProtocolException("installer.owner_transfer.concurrent_execution");
        }

        try
        {
            ArgumentNullException.ThrowIfNull(expected);
            expected.Validate();
            cancellationToken.ThrowIfCancellationRequested();
            InstallerOwnerTransferSnapshot current = await _store.LoadAsync(cancellationToken).ConfigureAwait(false)
                ?? throw new InstallerProtocolException("installer.owner_transfer.state_missing");
            current.Validate();
            if (current != expected)
            {
                throw new InstallerProtocolException("installer.owner_transfer.state_changed");
            }

            while (current.Journal.Phase < InstallerOwnerTransferPhase.InstallerAccessTransferred)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var next = (InstallerOwnerTransferPhase)((int)current.Journal.Phase + 1);
                await _executor.ApplyAndVerifyAsync(current.Journal, next, cancellationToken).ConfigureAwait(false);
                current = await _store.SaveAsync(
                    current.Journal.TransitionTo(next), current.ContentHash, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await _executor.VerifyCompletedAsync(current.Journal, cancellationToken).ConfigureAwait(false);
            if (current.Journal.Phase != InstallerOwnerTransferPhase.Verified)
            {
                current = await _store.SaveAsync(
                    current.Journal.TransitionTo(InstallerOwnerTransferPhase.Verified),
                    current.ContentHash, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            InstallerTransactionSnapshot continuation = InstallerTransactionSnapshot.Create(current.Journal.Continuation);
            await _store.ClearVerifiedAsync(
                continuation.Journal.TransactionId, current.ContentHash, cancellationToken).ConfigureAwait(false);
            return continuation;
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }
}
