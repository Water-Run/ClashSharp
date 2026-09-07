using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Certificates;

/// <summary>
/// Releases existing private ownership evidence with strict account binding and compare-and-swap
/// recovery. Missing or malformed evidence never grants permission to manufacture a ledger.
/// </summary>
public sealed class InstallerArchivedCertificateStore : IInstallerArchivedCertificateStore
{
    private readonly string _targetSid;
    private readonly IInstallerArchivedCertificatePersistence _persistence;
    private int _active;

    /// <summary>Initializes a lazy store without opening files or acquiring helper authority.</summary>
    /// <param name="targetSid">Canonical target SID derived from the authenticated parent.</param>
    /// <param name="persistence">Account-bound private persistence owned by the helper session.</param>
    public InstallerArchivedCertificateStore(string targetSid, IInstallerArchivedCertificatePersistence persistence)
    {
        InstallerProtocolValidation.ValidateTargetSid(targetSid);
        ArgumentNullException.ThrowIfNull(persistence);
        _targetSid = targetSid;
        _persistence = persistence;
    }

    /// <inheritdoc />
    public async Task<InstallerCertificateOwnershipSnapshot?> LoadAsync(CancellationToken cancellationToken)
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
    public async Task<InstallerCertificateOwnershipSnapshot> ReleaseReferenceAsync(
        InstallerCertificateOwnershipSnapshot expected, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            ValidateSnapshot(expected, _targetSid);
            InstallerCertificateOwnershipSnapshot? before = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            RequireExpected(before, expected);
            if (expected.Ledger.ManagedReferenceCount == 0)
            {
                return expected;
            }

            InstallerCertificateOwnershipLedger released = expected.Ledger.PrepareRemoval();
            byte[] bytes = InstallerCertificateOwnershipCodec.Serialize(released);
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

                const string uncertain = "installer.certificate_archive.write_state_uncertain";
                InstallerCertificateOwnershipSnapshot? after = await ReconcileAsync(uncertain).ConfigureAwait(false);
                if (after?.Ledger == released)
                {
                    return after;
                }
                if (cancelled && after == before)
                {
                    throw new OperationCanceledException(cancellationToken);
                }
                throw new InstallerStateUncertainException(uncertain);
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
    public async Task ClearAsync(InstallerCertificateOwnershipSnapshot expected, CancellationToken cancellationToken)
    {
        Enter();
        try
        {
            ValidateSnapshot(expected, _targetSid);
            if (expected.Ledger.ManagedReferenceCount != 0)
            {
                throw new InstallerProtocolException("installer.certificate_archive.reference_active");
            }
            InstallerCertificateOwnershipSnapshot? before = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            RequireExpected(before, expected);
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

            const string uncertain = "installer.certificate_archive.clear_state_uncertain";
            InstallerCertificateOwnershipSnapshot? after = await ReconcileAsync(uncertain).ConfigureAwait(false);
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
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    internal static void ValidateSnapshot(InstallerCertificateOwnershipSnapshot snapshot, string targetSid)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Ledger);
        snapshot.Ledger.Validate();
        if (snapshot.Ledger.TargetSid != targetSid)
        {
            throw new InstallerProtocolException("installer.certificate_archive.target_sid_mismatch");
        }
        byte[] canonical = InstallerCertificateOwnershipCodec.Serialize(snapshot.Ledger);
        try
        {
            if (snapshot.ContentHash != Convert.ToHexStringLower(SHA256.HashData(canonical)))
            {
                throw new InstallerProtocolException("installer.certificate_archive.snapshot_invalid");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(canonical);
        }
    }

    private async Task<InstallerCertificateOwnershipSnapshot?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[]? bytes;
        try
        {
            bytes = await _persistence.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerProtocolException("installer.certificate_archive.read_failed");
        }
        byte[]? canonical = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (bytes is null)
            {
                return null;
            }
            InstallerCertificateOwnershipLedger ledger;
            try
            {
                ledger = InstallerCertificateOwnershipCodec.Parse(bytes);
                canonical = InstallerCertificateOwnershipCodec.Serialize(ledger);
            }
            catch (InstallerProtocolException)
            {
                // Parser details and private account evidence must not reach the parent.
                throw new InstallerProtocolException("installer.certificate_archive.document_invalid");
            }
            if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            {
                throw new InstallerProtocolException("installer.certificate_archive.document_invalid");
            }
            if (ledger.TargetSid != _targetSid)
            {
                throw new InstallerProtocolException("installer.certificate_archive.target_sid_mismatch");
            }
            return new(ledger, Convert.ToHexStringLower(SHA256.HashData(bytes)));
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

    private async Task<InstallerCertificateOwnershipSnapshot?> ReconcileAsync(string diagnosticCode)
    {
        try
        {
            // The write or delete may have committed before its acknowledgement was lost.
            // Keep all caller-owned authority until this observation has actually completed.
            return await LoadCoreAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InstallerProtocolException
            or InstallerStateUncertainException or OperationCanceledException)
        {
            throw new InstallerStateUncertainException(diagnosticCode);
        }
    }

    private static void RequireExpected(InstallerCertificateOwnershipSnapshot? current,
        InstallerCertificateOwnershipSnapshot expected)
    {
        if (current != expected)
        {
            throw new InstallerProtocolException("installer.certificate_archive.state_conflict");
        }
    }

    private void Enter()
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InstallerProtocolException("installer.certificate_archive.concurrent_access");
        }
    }

    private static bool MayHaveMutated(Exception exception) => exception is IOException
        or UnauthorizedAccessException or InstallerStateUncertainException or OperationCanceledException;
}
