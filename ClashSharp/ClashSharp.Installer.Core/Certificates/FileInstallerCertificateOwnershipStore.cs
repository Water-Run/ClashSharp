using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Transactions;

namespace ClashSharp.Installer.Certificates;

/// <summary>Persists certificate ownership through guarded same-directory atomic replacement.</summary>
public sealed class FileInstallerCertificateOwnershipStore :
    IInstallerCertificateOwnershipStore,
    IDisposable
{
    /// <summary>Gets the fixed ownership filename below the protected installer-state root.</summary>
    public const string LedgerFileName = "certificate-ownership-v1.json";

    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly string _rootPath;
    private readonly string _ledgerPath;
    private readonly IInstallerTransactionRootGuard _rootGuard;
    private bool _disposed;

    /// <summary>Initializes a store without touching the filesystem.</summary>
    /// <param name="rootPath">Absolute, machine-protected installer-state root.</param>
    /// <param name="rootGuard">Owner, ACL, ancestor, and reparse-point verifier.</param>
    public FileInstallerCertificateOwnershipStore(
        string rootPath,
        IInstallerTransactionRootGuard rootGuard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(rootGuard);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The installer-state root must be absolute.", nameof(rootPath));
        }

        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _ledgerPath = ValidateContainedPath(Path.Combine(_rootPath, LedgerFileName));
        _rootGuard = rootGuard;
    }

    /// <inheritdoc />
    public async Task<InstallerCertificateOwnershipSnapshot?> LoadAsync(
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rootGuard.EnsureProtectedAsync(_rootPath, cancellationToken).ConfigureAwait(false);
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<InstallerCertificateOwnershipSnapshot> SaveAsync(
        InstallerCertificateOwnershipLedger ledger,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(ledger);
        ledger.Validate();
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rootGuard.EnsureProtectedAsync(_rootPath, cancellationToken).ConfigureAwait(false);
            InstallerCertificateOwnershipSnapshot? current = await LoadCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            ValidateReplacement(ledger, current, expectedCurrentHash);
            byte[] bytes = InstallerCertificateOwnershipCodec.Serialize(ledger);
            string contentHash = ComputeHash(bytes);
            string temporaryPath = ValidateContainedPath(
                Path.Combine(_rootPath, $".{LedgerFileName}.{Guid.NewGuid():N}.tmp"));

            try
            {
                await WriteThroughAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _ = LedgerTargetExistsAndIsOrdinaryFile();
                File.Move(temporaryPath, _ledgerPath, overwrite: true);
                byte[] persisted = await ReadBoundedAsync(_ledgerPath, CancellationToken.None)
                    .ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(bytes, persisted))
                {
                    throw new InstallerProtocolException(
                        "installer.certificate.write_verification_failed");
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return new InstallerCertificateOwnershipSnapshot(ledger, contentHash);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearUnreferencedAsync(
        string ledgerId,
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InstallerProtocolValidation.ValidateLowerHex256(
            ledgerId,
            "installer.certificate.ledger_id_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            expectedCurrentHash,
            "installer.certificate.content_hash_invalid");
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rootGuard.EnsureProtectedAsync(_rootPath, cancellationToken).ConfigureAwait(false);
            InstallerCertificateOwnershipSnapshot? current = await LoadCoreAsync(cancellationToken)
                .ConfigureAwait(false);
            if (current is null
                || current.Ledger.ManagedReferenceCount != 0
                || !string.Equals(current.Ledger.LedgerId, ledgerId, StringComparison.Ordinal)
                || !string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new InstallerProtocolException("installer.certificate.clear_conflict");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = LedgerTargetExistsAndIsOrdinaryFile();
            File.Delete(_ledgerPath);
            if (LedgerTargetExistsAndIsOrdinaryFile())
            {
                throw new InstallerProtocolException("installer.certificate.clear_failed");
            }
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <summary>Releases the process-local I/O serialization gate.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _ioGate.Dispose();
        _disposed = true;
    }

    private async Task<InstallerCertificateOwnershipSnapshot?> LoadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (!LedgerTargetExistsAndIsOrdinaryFile())
        {
            return null;
        }

        byte[] bytes = await ReadBoundedAsync(_ledgerPath, cancellationToken).ConfigureAwait(false);
        InstallerCertificateOwnershipLedger ledger = InstallerCertificateOwnershipCodec.Parse(bytes);
        return new InstallerCertificateOwnershipSnapshot(ledger, ComputeHash(bytes));
    }

    private static void ValidateReplacement(
        InstallerCertificateOwnershipLedger ledger,
        InstallerCertificateOwnershipSnapshot? current,
        string? expectedCurrentHash)
    {
        if (current is null)
        {
            if (expectedCurrentHash is not null || ledger.Generation != 1)
            {
                throw new InstallerProtocolException("installer.certificate.write_conflict");
            }

            return;
        }

        bool exactCurrent = current.Ledger == ledger;
        bool ownershipAdvance = current.Ledger.WasPreExisting
            && current.Ledger.ManagedReferenceCount == 1
            && current.Ledger.TakeOwnershipForMissingCertificate() == ledger;
        bool removalAdvance = current.Ledger.ManagedReferenceCount == 1
            && current.Ledger.PrepareRemoval() == ledger;
        if (!string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.Ordinal)
            || !string.Equals(current.Ledger.LedgerId, ledger.LedgerId, StringComparison.Ordinal)
            || !(exactCurrent || ownershipAdvance || removalAdvance))
        {
            throw new InstallerProtocolException("installer.certificate.write_conflict");
        }
    }

    private static async Task WriteThroughAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        FileStreamOptions options = new()
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        await using FileStream stream = new(path, options);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        CancellationToken cancellationToken)
    {
        FileInfo information = new(path);
        if (information.Length is < 1 or > InstallerCertificateOwnershipCodec.MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.certificate.size_invalid");
        }

        byte[] bytes = new byte[checked((int)information.Length)];
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        if (stream.ReadByte() != -1)
        {
            throw new InstallerProtocolException("installer.certificate.size_changed");
        }

        return bytes;
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexStringLower(SHA256.HashData(bytes));

    private string ValidateContainedPath(string candidate)
    {
        string fullPath = Path.GetFullPath(candidate);
        string prefix = _rootPath + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new InstallerProtocolException("installer.certificate.path_invalid");
        }

        return fullPath;
    }

    private bool LedgerTargetExistsAndIsOrdinaryFile()
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(_ledgerPath);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InstallerProtocolException(
                "installer.certificate.target_query_failed",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstallerProtocolException("installer.certificate.reparse_point_rejected");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InstallerProtocolException("installer.certificate.target_not_file");
        }

        return true;
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // A uniquely named abandoned temporary file is never trusted as ownership state.
        }
        catch (UnauthorizedAccessException)
        {
            // The protected root guard reports persistent permission faults on the next invocation.
        }
    }
}
