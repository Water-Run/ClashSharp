using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Retirement;

/// <summary>
/// Persists the dedicated uninstall journal below a caller-pinned authority-only root. Its caller
/// owns the machine-wide lease until each operation and any uncancelled reconciliation has drained.
/// </summary>
public interface IInstallerRetiredUninstallPersistence
{
    /// <summary>Returns owned bounded bytes or proven absence from the fixed private leaf.</summary>
    Task<byte[]?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Atomically writes the fixed private leaf with authority-only file permissions.</summary>
    Task WriteAtomicallyAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Deletes the fixed private leaf after an exact state comparison by the store.</summary>
    Task DeleteAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Owns recoverable account-copy removal state separately from the shared owner's ordinary journal.
/// A single private journal blocks other machine transactions until this exact account resumes it.
/// </summary>
public sealed class InstallerRetiredUninstallStore : IInstallerTransactionStore
{
    /// <summary>Gets the fixed private journal leaf below InstallerAuthority/v1.</summary>
    public const string JournalFileName = "retired-uninstall-v1.json";

    private readonly string _targetSid;
    private readonly IInstallerRetiredUninstallPersistence _persistence;
    private int _active;

    /// <summary>Initializes a lazy store bound to the authenticated retired account.</summary>
    /// <param name="targetSid">Canonical target SID independently obtained from the parent process.</param>
    /// <param name="persistence">Private fixed-path persistence owned by the helper authority scope.</param>
    public InstallerRetiredUninstallStore(string targetSid, IInstallerRetiredUninstallPersistence persistence)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        ArgumentNullException.ThrowIfNull(persistence);
        _targetSid = targetSid;
        _persistence = persistence;
    }

    /// <inheritdoc />
    public async Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        Enter();
        try { return await LoadCoreAsync(cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref _active, 0); }
    }

    /// <inheritdoc />
    public async Task<InstallerTransactionSnapshot> SaveAsync(InstallerTransactionJournal journal,
        string? expectedCurrentHash, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            InstallerTransactionSnapshot desired = InstallerTransactionSnapshot.Create(journal);
            ValidateTarget(desired);
            InstallerTransactionSnapshot? before = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (before is null)
            {
                if (expectedCurrentHash is not null || journal.Phase != InstallerTransactionPhase.Prepared)
                {
                    throw Conflict();
                }
            }
            else if (before.ContentHash != expectedCurrentHash || before.Journal.TransitionTo(journal.Phase) != journal)
            {
                throw Conflict();
            }
            if (before == desired)
            {
                return before;
            }
            byte[] bytes = InstallerTransactionCodec.Serialize(journal);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                bool cancelled = false;
                try { await _persistence.WriteAtomicallyAsync(bytes, cancellationToken).ConfigureAwait(false); }
                catch (Exception exception) when (MayHaveMutated(exception))
                {
                    cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
                }
                const string uncertain = "installer.retired_uninstall.write_state_uncertain";
                InstallerTransactionSnapshot? after = await ReconcileAsync(uncertain).ConfigureAwait(false);
                if (after == desired)
                {
                    return after;
                }
                if (cancelled && after == before)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new InstallerStateUncertainException(uncertain);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    /// <inheritdoc />
    public async Task ClearVerifiedAsync(string transactionId, string expectedCurrentHash, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            InstallerProtocolValidation.ValidateLowerHex256(transactionId, "installer.retired_uninstall.transaction_id_invalid");
            InstallerProtocolValidation.ValidateLowerHex256(expectedCurrentHash, "installer.retired_uninstall.content_hash_invalid");
            InstallerTransactionSnapshot? before = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (before is null || before.Journal.TransactionId != transactionId || before.ContentHash != expectedCurrentHash
                || before.Journal.Phase != InstallerTransactionPhase.Verified)
            {
                throw Conflict();
            }
            cancellationToken.ThrowIfCancellationRequested();
            bool cancelled = false;
            try { await _persistence.DeleteAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception exception) when (MayHaveMutated(exception))
            {
                cancelled = exception is OperationCanceledException && cancellationToken.IsCancellationRequested;
            }
            const string uncertain = "installer.retired_uninstall.clear_state_uncertain";
            InstallerTransactionSnapshot? after = await ReconcileAsync(uncertain).ConfigureAwait(false);
            if (after is null)
            {
                return;
            }
            if (cancelled && after == before)
            {
                throw new OperationCanceledException(cancellationToken);
            }
            throw new InstallerStateUncertainException(uncertain);
        }
        finally { Volatile.Write(ref _active, 0); }
    }

    private async Task<InstallerTransactionSnapshot?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? bytes;
        try { bytes = await _persistence.ReadAsync(cancellationToken).ConfigureAwait(false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.read_failed");
        }
        byte[]? canonical = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                return null;
            }
            InstallerTransactionJournal journal;
            try
            {
                journal = InstallerTransactionCodec.Parse(bytes);
                canonical = InstallerTransactionCodec.Serialize(journal);
            }
            catch (InstallerProtocolException)
            {
                throw new InstallerProtocolException("installer.retired_uninstall.document_invalid");
            }
            if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            {
                throw new InstallerProtocolException("installer.retired_uninstall.document_invalid");
            }
            InstallerTransactionSnapshot snapshot = InstallerTransactionSnapshot.Create(journal);
            ValidateTarget(snapshot);
            return snapshot;
        }
        finally
        {
            if (bytes is not null)
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
            if (canonical is not null)
            {
                CryptographicOperations.ZeroMemory(canonical);
            }
        }
    }

    private void ValidateTarget(InstallerTransactionSnapshot snapshot)
    {
        InstallerRetiredUninstallProtocol.Validate(snapshot);
        if (snapshot.Journal.TargetSid != _targetSid)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.target_sid_mismatch");
        }
    }

    private async Task<InstallerTransactionSnapshot?> ReconcileAsync(string code)
    {
        try { return await LoadCoreAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception exception) when (exception is InstallerProtocolException or InstallerStateUncertainException or OperationCanceledException)
        {
            throw new InstallerStateUncertainException(code);
        }
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InstallerProtocolException("installer.retired_uninstall.concurrent_access");
        }
    }

    private static InstallerProtocolException Conflict() => new("installer.retired_uninstall.state_conflict");
    private static bool MayHaveMutated(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InstallerStateUncertainException or OperationCanceledException;
}
