using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Ownership;

/// <summary>
/// Applies private journal compare-and-swap rules and observes durable outcomes after lost or
/// cancelled acknowledgements. This object owns no handles; the elevated session owns persistence.
/// The local call guard supplements, but never replaces, machine-wide authority exclusion.
/// </summary>
public sealed class InstallerOwnerTransferStore : IInstallerOwnerTransferStore
{
    private readonly IInstallerOwnerTransferPersistence _persistence;
    private int _active;

    /// <summary>Initializes a store without opening state or acquiring Windows authority.</summary>
    /// <param name="persistence">Private bounded persistence whose lifetime is owned by the caller.</param>
    public InstallerOwnerTransferStore(IInstallerOwnerTransferPersistence persistence)
    {
        ArgumentNullException.ThrowIfNull(persistence);
        _persistence = persistence;
    }

    /// <inheritdoc />
    public async Task<InstallerOwnerTransferSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    /// <inheritdoc />
    public async Task<InstallerOwnerTransferSnapshot> SaveAsync(
        InstallerOwnerTransferJournal journal,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            ArgumentNullException.ThrowIfNull(journal);
            journal.Validate();
            InstallerOwnerTransferSnapshot? before = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            InstallerOwnerTransferStatePolicy.ValidateSave(journal, before, expectedCurrentHash);
            if (before?.Journal == journal)
            {
                return before;
            }

            byte[] bytes = InstallerOwnerTransferCodec.Serialize(journal);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool cancelled = false;
                try
                {
                    await _persistence.WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (MayHaveMutated(exception))
                {
                    cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
                }

                const string uncertainCode = "installer.owner_transfer.write_state_uncertain";
                InstallerOwnerTransferSnapshot? after = await ReconcileAsync(uncertainCode).ConfigureAwait(false);
                if (after?.Journal == journal)
                {
                    return after;
                }

                if (cancelled && after == before)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                throw new InstallerStateUncertainException(uncertainCode);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    /// <inheritdoc />
    public async Task ClearVerifiedAsync(
        string transactionId,
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            InstallerProtocolValidation.ValidateLowerHex256(
                transactionId, "installer.owner_transfer.transaction_id_invalid");
            InstallerProtocolValidation.ValidateLowerHex256(
                expectedCurrentHash, "installer.owner_transfer.content_hash_invalid");
            InstallerOwnerTransferSnapshot? before = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            InstallerOwnerTransferStatePolicy.ValidateClear(before, transactionId, expectedCurrentHash);
            cancellationToken.ThrowIfCancellationRequested();
            bool cancelled = false;
            try
            {
                await _persistence.DeleteAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (MayHaveMutated(exception))
            {
                cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            }

            const string uncertainCode = "installer.owner_transfer.clear_state_uncertain";
            InstallerOwnerTransferSnapshot? after = await ReconcileAsync(uncertainCode).ConfigureAwait(false);
            if (after is null)
            {
                return;
            }

            if (cancelled && after == before)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            throw new InstallerStateUncertainException(uncertainCode);
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    private async Task<InstallerOwnerTransferSnapshot?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? bytes;
        try
        {
            bytes = await _persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerProtocolException("installer.owner_transfer.read_failed");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return bytes is null
                ? null
                : new InstallerOwnerTransferSnapshot(
                    InstallerOwnerTransferCodec.Parse(bytes),
                    Convert.ToHexStringLower(SHA256.HashData(bytes)));
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
    }

    private async Task<InstallerOwnerTransferSnapshot?> ReconcileAsync(string uncertainCode)
    {
        try
        {
            // Cancellation can arrive after the atomic rename/delete. Observe its outcome while
            // the caller still owns all authority leases instead of discarding committed evidence.
            return await LoadCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InstallerProtocolException
            or InstallerStateUncertainException or OperationCanceledException)
        {
            throw new InstallerStateUncertainException(uncertainCode);
        }
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InstallerProtocolException("installer.owner_transfer.concurrent_access");
        }
    }

    private static bool MayHaveMutated(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or InstallerStateUncertainException
            or OperationCanceledException;
}
