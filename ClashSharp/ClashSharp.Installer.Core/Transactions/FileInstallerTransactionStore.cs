using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Transactions;

/// <summary>Persists the fixed installer journal through guarded same-directory atomic replacement.</summary>
public sealed class FileInstallerTransactionStore : IInstallerTransactionStore, IDisposable
{
    /// <summary>Gets the fixed journal filename below the protected root.</summary>
    public const string JournalFileName = "transaction-v2.json";

    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly string _rootPath;
    private readonly string _journalPath;
    private readonly IInstallerTransactionRootGuard _rootGuard;
    private bool _disposed;

    /// <summary>Initializes a store without touching the filesystem.</summary>
    /// <param name="rootPath">Absolute, machine-protected installer journal root.</param>
    /// <param name="rootGuard">Windows-specific owner, ACL, and reparse-point verifier.</param>
    public FileInstallerTransactionStore(
        string rootPath,
        IInstallerTransactionRootGuard rootGuard)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        ArgumentNullException.ThrowIfNull(rootGuard);
        if (!Path.IsPathFullyQualified(rootPath))
        {
            throw new ArgumentException("The installer transaction root must be absolute.", nameof(rootPath));
        }

        _rootPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        _journalPath = ValidateContainedPath(Path.Combine(_rootPath, JournalFileName));
        _rootGuard = rootGuard;
    }

    /// <inheritdoc />
    public async Task<InstallerTransactionSnapshot?> LoadAsync(CancellationToken cancellationToken)
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
    public async Task<InstallerTransactionSnapshot> SaveAsync(
        InstallerTransactionJournal journal,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(journal);
        journal.Validate();
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rootGuard.EnsureProtectedAsync(_rootPath, cancellationToken).ConfigureAwait(false);
            InstallerTransactionSnapshot? current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            ValidateReplacement(journal, current, expectedCurrentHash);
            byte[] bytes = InstallerTransactionCodec.Serialize(journal);
            string contentHash = ComputeHash(bytes);
            string temporaryPath = ValidateContainedPath(
                Path.Combine(_rootPath, $".{JournalFileName}.{Guid.NewGuid():N}.tmp"));

            try
            {
                await WriteThroughAsync(temporaryPath, bytes, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                _ = JournalTargetExistsAndIsOrdinaryFile();
                File.Move(temporaryPath, _journalPath, overwrite: true);
                byte[] persisted = await ReadBoundedAsync(_journalPath, CancellationToken.None).ConfigureAwait(false);
                if (!CryptographicOperations.FixedTimeEquals(bytes, persisted))
                {
                    throw new InstallerProtocolException("installer.transaction.write_verification_failed");
                }
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return new InstallerTransactionSnapshot(journal, contentHash);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task ClearVerifiedAsync(
        string transactionId,
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        InstallerProtocolValidation.ValidateLowerHex256(
            transactionId,
            "installer.transaction.id_invalid");
        InstallerProtocolValidation.ValidateLowerHex256(
            expectedCurrentHash,
            "installer.transaction.content_hash_invalid");
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _rootGuard.EnsureProtectedAsync(_rootPath, cancellationToken).ConfigureAwait(false);
            InstallerTransactionSnapshot? current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (current is null
                || current.Journal.Phase != InstallerTransactionPhase.Verified
                || !string.Equals(current.Journal.TransactionId, transactionId, StringComparison.Ordinal)
                || !string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
            {
                throw new InstallerProtocolException("installer.transaction.clear_conflict");
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = JournalTargetExistsAndIsOrdinaryFile();
            File.Delete(_journalPath);
            if (JournalTargetExistsAndIsOrdinaryFile())
            {
                throw new InstallerProtocolException("installer.transaction.clear_failed");
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

    private async Task<InstallerTransactionSnapshot?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!JournalTargetExistsAndIsOrdinaryFile())
        {
            return null;
        }

        byte[] bytes = await ReadBoundedAsync(_journalPath, cancellationToken).ConfigureAwait(false);
        InstallerTransactionJournal journal = InstallerTransactionCodec.Parse(bytes);
        return new InstallerTransactionSnapshot(journal, ComputeHash(bytes));
    }

    private static void ValidateReplacement(
        InstallerTransactionJournal journal,
        InstallerTransactionSnapshot? current,
        string? expectedCurrentHash)
    {
        if (current is null)
        {
            if (expectedCurrentHash is not null || journal.Generation != 1)
            {
                throw new InstallerProtocolException("installer.transaction.write_conflict");
            }

            return;
        }

        if (!string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.Ordinal)
            || !string.Equals(current.Journal.TransactionId, journal.TransactionId, StringComparison.Ordinal)
            || current.Journal.TransitionTo(journal.Phase) != journal)
        {
            throw new InstallerProtocolException("installer.transaction.write_conflict");
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

    private static async Task<byte[]> ReadBoundedAsync(string path, CancellationToken cancellationToken)
    {
        FileInfo information = new(path);
        if (information.Length is < 1 or > InstallerTransactionCodec.MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.transaction.size_invalid");
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
            throw new InstallerProtocolException("installer.transaction.size_changed");
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
            throw new InstallerProtocolException("installer.transaction.path_invalid");
        }

        return fullPath;
    }

    private bool JournalTargetExistsAndIsOrdinaryFile()
    {
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(_journalPath);
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
                "installer.transaction.target_query_failed",
                exception);
        }

        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InstallerProtocolException("installer.transaction.reparse_point_rejected");
        }

        if ((attributes & FileAttributes.Directory) != 0)
        {
            throw new InstallerProtocolException("installer.transaction.target_not_file");
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
            // A uniquely named abandoned temporary file is never trusted as transaction state.
        }
        catch (UnauthorizedAccessException)
        {
            // The protected root guard reports persistent permission faults on the next invocation.
        }
    }
}
