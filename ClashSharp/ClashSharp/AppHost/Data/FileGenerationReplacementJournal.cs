using System;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Hosting.Data;

/// <summary>Persists one bounded, hashed replacement checkpoint with atomic publication and retained completion.</summary>
internal sealed class FileGenerationReplacementJournal : IGenerationReplacementJournal
{
    private const int MaximumBytes = 64 * 1024;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DataGenerationPathPolicy _paths;
    private readonly Action<string>? _checkpoint;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 24,
        Converters = { new JsonStringEnumConverter() },
    };

    public FileGenerationReplacementJournal(string applicationDataRoot, Action<string>? checkpoint = null)
    {
        _paths = new(applicationDataRoot);
        Path = System.IO.Path.Combine(_paths.DataRootPath, "replacement-transaction.json");
        _checkpoint = checkpoint;
    }

    internal string Path { get; }

    public async Task<GenerationReplacementCheckpoint?> ReadPendingAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            GenerationReplacementCheckpoint? current = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            return current is { Completed: false } ? current : null;
        }
        finally { _gate.Release(); }
    }

    public Task BeginAsync(Guid operationId, DataGenerationManifestSnapshot baseline, GenerationExternalStateSnapshot external, CancellationToken cancellationToken) =>
        WriteAsync(current => current is { Completed: false }
            ? throw new InvalidOperationException("A previous data replacement still requires recovery.")
            : new(1, 1, operationId, baseline, external, null, false), cancellationToken);

    public Task SetCandidateAsync(Guid operationId, DataGenerationDescriptor candidate, CancellationToken cancellationToken) =>
        WriteAsync(current =>
        {
            GenerationReplacementCheckpoint owned = RequireActive(current, operationId);
            if (owned.Candidate is not null && !owned.Candidate.IsSameGeneration(candidate)) { throw new InvalidOperationException("The recovery checkpoint already names another candidate."); }
            return owned with { Revision = checked(owned.Revision + 1), Candidate = candidate };
        }, cancellationToken);

    public Task CompleteAsync(Guid operationId, CancellationToken cancellationToken) =>
        WriteAsync(current =>
        {
            if (current is { Completed: true } && current.OperationId == operationId) { return current; }
            GenerationReplacementCheckpoint owned = RequireActive(current, operationId);
            return owned with { Revision = checked(owned.Revision + 1), Completed = true };
        }, cancellationToken);

    private async Task WriteAsync(Func<GenerationReplacementCheckpoint?, GenerationReplacementCheckpoint> update, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _paths.EnsureLayout();
            string lockPath = Path + ".lock";
            _paths.ValidateStagingPath(lockPath);
            await using FileStream ownership = new(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            GenerationReplacementCheckpoint? current = await ReadCoreAsync(cancellationToken).ConfigureAwait(false);
            GenerationReplacementCheckpoint next = update(current);
            Validate(next);
            if (ReferenceEquals(current, next)) { return; }
            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(next, Options);
            Envelope envelope = new(1, Convert.ToBase64String(payload), Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
            byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
            if (bytes.Length > MaximumBytes) { throw new InvalidDataException("The replacement checkpoint exceeds its size limit."); }
            string staging = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            _paths.ValidateStagingPath(staging);
            try
            {
                await using (FileStream output = new(staging, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await output.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    output.Flush(flushToDisk: true);
                }
                _checkpoint?.Invoke("before-promotion");
                cancellationToken.ThrowIfCancellationRequested();
                _paths.ValidateStagingPath(Path);
                File.Move(staging, Path, overwrite: true);
                using (FileStream durable = new(Path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read)) { durable.Flush(flushToDisk: true); }
                _checkpoint?.Invoke("after-promotion");
                GenerationReplacementCheckpoint? verified = await ReadCoreAsync(CancellationToken.None).ConfigureAwait(false);
                if (verified is null || !JsonSerializer.SerializeToUtf8Bytes(verified, Options).AsSpan().SequenceEqual(payload))
                {
                    throw new IOException("The durable replacement checkpoint did not match its verified input.");
                }
            }
            finally { if (File.Exists(staging)) { File.Delete(staging); } }
        }
        finally { _gate.Release(); }
    }

    private async Task<GenerationReplacementCheckpoint?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _paths.ValidateStagingPath(Path);
        FileAttributes attributes;
        try { attributes = File.GetAttributes(Path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
        if ((attributes & FileAttributes.Directory) != 0) { throw new InvalidDataException("The replacement checkpoint is occupied by a directory."); }
        await using FileStream input = new(Path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 81920, FileOptions.Asynchronous);
        if (input.Length is <= 0 or > MaximumBytes) { throw new InvalidDataException("The replacement checkpoint has an invalid size."); }
        byte[] bytes = new byte[checked((int)input.Length)];
        await input.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        Envelope encoded = JsonSerializer.Deserialize<Envelope>(bytes, Options) ?? throw new InvalidDataException("The replacement checkpoint is empty.");
        if (encoded.Version != 1 || !DataGenerationManifestSnapshot.IsCanonicalContentHash(encoded.Hash)) { throw new InvalidDataException("The replacement checkpoint header is invalid."); }
        byte[] payload = Convert.FromBase64String(encoded.Payload);
        if (payload.Length > MaximumBytes || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), Convert.FromHexString(encoded.Hash)))
        {
            throw new InvalidDataException("The replacement checkpoint hash is invalid.");
        }
        GenerationReplacementCheckpoint record = JsonSerializer.Deserialize<GenerationReplacementCheckpoint>(payload, Options)
            ?? throw new InvalidDataException("The replacement checkpoint payload is empty.");
        Validate(record);
        return record;
    }

    private void Validate(GenerationReplacementCheckpoint record)
    {
        if (record.Version != 1 || record.Revision < 1 || record.OperationId == Guid.Empty || record.Baseline is null || record.External is null)
        {
            throw new InvalidDataException("The replacement checkpoint identity is invalid.");
        }
        _paths.ValidateDescriptor(record.Baseline.Descriptor);
        if (!record.Baseline.Descriptor.IsSameGeneration(record.External.Generation) || record.External.Appearance is null || record.External.Network is null)
        {
            throw new InvalidDataException("The saved external state does not belong to the baseline.");
        }
        if (record.Candidate is { } candidate)
        {
            _paths.ValidateDescriptor(candidate);
            if (candidate.GenerationId == record.Baseline.Descriptor.GenerationId
                || record.Baseline.HighestGenerationNumber == long.MaxValue
                || candidate.GenerationNumber != record.Baseline.HighestGenerationNumber + 1)
            {
                throw new InvalidDataException("The saved candidate is not the next data generation.");
            }
        }
    }

    private static GenerationReplacementCheckpoint RequireActive(GenerationReplacementCheckpoint? current, Guid operationId) =>
        current is { Completed: false } && current.OperationId == operationId ? current
            : throw new InvalidOperationException("The replacement checkpoint belongs to another operation or is already complete.");

    private sealed record Envelope(int Version, string Payload, string Hash);
}
