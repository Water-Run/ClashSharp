using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.Infrastructure.Recovery;

/// <summary>Persists one hashed mutation journal through same-directory atomic replacement.</summary>
public sealed class FileMutationJournalStore : IMutationJournalStore
{
    /// <summary>Gets the fixed journal filename below the protected recovery root.</summary>
    public const string JournalFileName = "mutation-journal.json";

    private const int EnvelopeSchemaVersion = 1;
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly string _rootPath;
    private readonly string _journalPath;
    private readonly Action<MutationJournalWriteStage>? _writeObserver;

    /// <summary>Initializes a store below the supplied dedicated recovery root without touching the filesystem.</summary>
    /// <param name="rootPath">Absolute dedicated recovery root.</param>
    public FileMutationJournalStore(string rootPath)
        : this(rootPath, null)
    {
    }

    internal FileMutationJournalStore(
        string rootPath,
        Action<MutationJournalWriteStage>? writeObserver)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = RecoveryRootPolicy.NormalizeAbsoluteRoot(rootPath);
        _journalPath = RecoveryRootPolicy.ValidateContainedTarget(
            _rootPath,
            Path.Combine(_rootPath, JournalFileName));
        _writeObserver = writeObserver;
    }

    /// <inheritdoc />
    public async Task<MutationJournalSnapshot?> LoadAsync(CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<MutationJournalSnapshot> SaveAsync(
        MutationJournal journal,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RecoveryRootPolicy.EnsureProtectedRoot(_rootPath);
            MutationJournalSnapshot? current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            ValidateNextGeneration(journal, current, expectedCurrentHash);
            ValidateJournal(journal);

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(journal, SerializerOptions);
            string contentHash = ComputeHash(payload);
            JournalEnvelope envelope = new(
                EnvelopeSchemaVersion,
                Convert.ToBase64String(payload),
                contentHash);
            byte[] envelopeBytes = JsonSerializer.SerializeToUtf8Bytes(envelope, SerializerOptions);
            string temporaryPath = RecoveryRootPolicy.ValidateContainedTarget(
                _rootPath,
                Path.Combine(_rootPath, $".{JournalFileName}.{Guid.NewGuid():N}.tmp"));
            string backupPath = RecoveryRootPolicy.ValidateContainedTarget(
                _rootPath,
                Path.Combine(_rootPath, $".{JournalFileName}.previous"));

            try
            {
                await WriteAndFlushAsync(temporaryPath, envelopeBytes, cancellationToken).ConfigureAwait(false);
                _writeObserver?.Invoke(MutationJournalWriteStage.AfterTemporaryFlush);
                cancellationToken.ThrowIfCancellationRequested();
                _writeObserver?.Invoke(MutationJournalWriteStage.BeforePromotion);
                Promote(temporaryPath, backupPath);
                _writeObserver?.Invoke(MutationJournalWriteStage.AfterPromotion);
            }
            finally
            {
                TryDelete(temporaryPath);
            }

            return new MutationJournalSnapshot(journal, contentHash);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task DeleteAsync(
        Guid operationId,
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("The operation identifier cannot be empty.", nameof(operationId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(expectedCurrentHash);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            MutationJournalSnapshot? current = await LoadCoreAsync(cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return;
            }

            if (current.Journal.OperationId != operationId
                || !string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.ConcurrencyConflict,
                    "The durable journal changed before cleanup.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Delete(_journalPath);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private async Task<MutationJournalSnapshot?> LoadCoreAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(_rootPath) || !File.Exists(_journalPath))
        {
            return null;
        }

        RecoveryRootPolicy.EnsureProtectedRoot(_rootPath);
        try
        {
            byte[] envelopeBytes = await File.ReadAllBytesAsync(_journalPath, cancellationToken).ConfigureAwait(false);
            JournalEnvelope envelope = JsonSerializer.Deserialize<JournalEnvelope>(envelopeBytes, SerializerOptions)
                ?? throw CreateCorruptException("The mutation journal envelope is empty.");
            if (envelope.SchemaVersion != EnvelopeSchemaVersion)
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.UnsupportedSchema,
                    $"Mutation journal envelope schema '{envelope.SchemaVersion}' is unsupported.");
            }

            byte[] payload = Convert.FromBase64String(envelope.Payload);
            string actualHash = ComputeHash(payload);
            if (!string.Equals(actualHash, envelope.ContentHash, StringComparison.OrdinalIgnoreCase))
            {
                throw CreateCorruptException("The mutation journal payload hash does not match its envelope.");
            }

            MutationJournal journal = JsonSerializer.Deserialize<MutationJournal>(payload, SerializerOptions)
                ?? throw CreateCorruptException("The mutation journal payload is empty.");
            ValidateJournal(journal);
            return new MutationJournalSnapshot(journal, actualHash);
        }
        catch (MutationJournalStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            throw CreateCorruptException("The mutation journal cannot be parsed.", exception);
        }
    }

    private static void ValidateNextGeneration(
        MutationJournal journal,
        MutationJournalSnapshot? current,
        string? expectedCurrentHash)
    {
        if (current is null)
        {
            if (expectedCurrentHash is not null)
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.ConcurrencyConflict,
                    "No durable journal exists for the supplied expected hash.");
            }

            if (journal.Generation != 1)
            {
                throw new MutationJournalStoreException(
                    MutationJournalStoreError.InvalidGeneration,
                    "The first durable journal generation must be one.");
            }

            return;
        }

        if (!string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.OrdinalIgnoreCase)
            || current.Journal.OperationId != journal.OperationId)
        {
            throw new MutationJournalStoreException(
                MutationJournalStoreError.ConcurrencyConflict,
                "The durable journal changed before the next generation was saved.");
        }

        if (journal.Generation != current.Journal.Generation + 1)
        {
            throw new MutationJournalStoreException(
                MutationJournalStoreError.InvalidGeneration,
                "A mutation journal generation must advance exactly once.");
        }
    }

    private static void ValidateJournal(MutationJournal journal)
    {
        if (journal.SchemaVersion != MutationJournal.CurrentSchemaVersion)
        {
            throw new MutationJournalStoreException(
                MutationJournalStoreError.UnsupportedSchema,
                $"Mutation journal schema '{journal.SchemaVersion}' is unsupported.");
        }

        if (journal.OperationId == Guid.Empty
            || string.IsNullOrWhiteSpace(journal.OperationType)
            || journal.Generation < 1
            || string.IsNullOrWhiteSpace(journal.BaselineHash)
            || string.IsNullOrWhiteSpace(journal.DesiredHash)
            || journal.Steps is null
            || journal.PhaseCompleted && !journal.PhaseIntentRecorded
            || journal.Steps.Any(step => string.IsNullOrWhiteSpace(step.Name) || step.Completed && !step.IntentRecorded)
            || journal.Steps.Select(step => step.Name).Distinct(StringComparer.Ordinal).Count() != journal.Steps.Count
            || journal.HasCommitMarker && journal.Phase is not (MutationJournalPhase.Committed or MutationJournalPhase.CleaningUp or MutationJournalPhase.Recovering)
            || !journal.HasCommitMarker && journal.Phase == MutationJournalPhase.CleaningUp)
        {
            throw CreateCorruptException("The mutation journal contains invalid required fields.");
        }
    }

    private static async Task WriteAndFlushAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private void Promote(string temporaryPath, string backupPath)
    {
        if (File.Exists(_journalPath))
        {
            TryDelete(backupPath);
            File.Replace(temporaryPath, _journalPath, backupPath, ignoreMetadataErrors: false);
            TryDelete(backupPath);
            return;
        }

        File.Move(temporaryPath, _journalPath);
    }

    private static string ComputeHash(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static MutationJournalStoreException CreateCorruptException(
        string message,
        Exception? innerException = null)
    {
        return new MutationJournalStoreException(MutationJournalStoreError.Corrupt, message, innerException);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.Web);
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private sealed record JournalEnvelope(int SchemaVersion, string Payload, string ContentHash);
}

internal enum MutationJournalWriteStage
{
    AfterTemporaryFlush,
    BeforePromotion,
    AfterPromotion,
}
