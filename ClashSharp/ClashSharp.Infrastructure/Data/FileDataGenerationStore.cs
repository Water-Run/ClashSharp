using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Infrastructure.Data;

/// <summary>Persists a hashed current-generation manifest through same-directory atomic replacement.</summary>
public sealed partial class FileDataGenerationStore : IDataGenerationStore
{
    private const int MaximumManifestBytes = 64 * 1024;
    private readonly SemaphoreSlim _ioGate = new(1, 1);
    private readonly DataGenerationPathPolicy _pathPolicy;
    private readonly IDataGenerationFaultInjector _faultInjector;

    /// <summary>Initializes a store without touching the filesystem.</summary>
    /// <param name="applicationDataRoot">Absolute application-local data root.</param>
    /// <param name="faultInjector">Optional deterministic persistence fault injector.</param>
    public FileDataGenerationStore(
        string applicationDataRoot,
        IDataGenerationFaultInjector? faultInjector = null)
    {
        _pathPolicy = new DataGenerationPathPolicy(applicationDataRoot);
        _faultInjector = faultInjector ?? new NullDataGenerationFaultInjector();
    }

    /// <inheritdoc />
    public async Task<DataGenerationManifestSnapshot?> LoadCurrentAsync(
        CancellationToken cancellationToken)
    {
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await LoadCoreAsync(
                    manifestLockHeld: false,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DataGenerationStoreException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateUnavailableException(
                "The current-generation manifest could not be read.",
                exception);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DataGenerationManifestSnapshot> PromoteAsync(
        DataGenerationDescriptor descriptor,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ValidateExpectedHash(expectedCurrentHash, allowNull: true);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pathPolicy.EnsureLayout();
            await using FileStream manifestLock =
                await AcquireManifestLockAsync(cancellationToken).ConfigureAwait(false);
            _pathPolicy.ValidateDescriptor(descriptor);
            DataGenerationManifestSnapshot? current =
                await LoadCoreAsync(
                        manifestLockHeld: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidatePromotion(descriptor, current, expectedCurrentHash);
            long manifestRevision = current is null
                ? 1
                : checked(current.ManifestRevision + 1);
            return await WriteManifestAsync(
                    descriptor,
                    manifestRevision,
                    descriptor.GenerationNumber,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DataGenerationStoreException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidGeneration,
                "The manifest revision cannot advance further.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateUnavailableException(
                "The next data generation could not be promoted.",
                exception);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<DataGenerationManifestSnapshot> RestoreAsync(
        DataGenerationManifestSnapshot baseline,
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ValidateExpectedHash(expectedCurrentHash, allowNull: false);
        await _ioGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _pathPolicy.EnsureLayout();
            await using FileStream manifestLock =
                await AcquireManifestLockAsync(cancellationToken).ConfigureAwait(false);
            _pathPolicy.ValidateDescriptor(baseline.Descriptor);
            DataGenerationManifestCodec.ValidateSnapshot(baseline, _pathPolicy);
            DataGenerationManifestSnapshot? current =
                await LoadCoreAsync(
                        manifestLockHeld: true,
                        cancellationToken)
                    .ConfigureAwait(false);
            ValidateRestoration(baseline, current, expectedCurrentHash);
            return await WriteManifestAsync(
                    baseline.Descriptor,
                    checked(current!.ManifestRevision + 1),
                    current.HighestGenerationNumber,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (DataGenerationStoreException)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (OverflowException exception)
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidGeneration,
                "The manifest revision cannot advance further.",
                exception);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw CreateUnavailableException(
                "The baseline data generation could not be restored.",
                exception);
        }
        finally
        {
            _ioGate.Release();
        }
    }

    private static void ValidatePromotion(
        DataGenerationDescriptor descriptor,
        DataGenerationManifestSnapshot? current,
        string? expectedCurrentHash)
    {
        if (current is null)
        {
            if (expectedCurrentHash is not null)
            {
                throw new DataGenerationStoreException(
                    DataGenerationStoreError.ConcurrencyConflict,
                    "No current-generation manifest exists for the expected hash.");
            }

            if (descriptor.GenerationNumber != 1)
            {
                throw new DataGenerationStoreException(
                    DataGenerationStoreError.InvalidGeneration,
                    "The first data generation must have sequence number one.");
            }

            return;
        }

        if (!string.Equals(
                current.ContentHash,
                expectedCurrentHash,
                StringComparison.Ordinal))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.ConcurrencyConflict,
                "The current-generation manifest changed before promotion.");
        }

        bool numberIsNext = current.HighestGenerationNumber < long.MaxValue
            && descriptor.GenerationNumber == current.HighestGenerationNumber + 1;
        if (!numberIsNext || descriptor.GenerationId == current.Descriptor.GenerationId)
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidGeneration,
                "The promoted generation must use a new identity and the next high-water number.");
        }
    }

    private static void ValidateRestoration(
        DataGenerationManifestSnapshot baseline,
        DataGenerationManifestSnapshot? current,
        string expectedCurrentHash)
    {
        if (current is null
            || !string.Equals(current.ContentHash, expectedCurrentHash, StringComparison.Ordinal))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.ConcurrencyConflict,
                "The promoted manifest changed before restoration.");
        }

        bool manifestIsImmediateSuccessor = baseline.ManifestRevision < long.MaxValue
            && current.ManifestRevision == baseline.ManifestRevision + 1;
        bool generationIsImmediateSuccessor = baseline.HighestGenerationNumber < long.MaxValue
            && current.HighestGenerationNumber == baseline.HighestGenerationNumber + 1
            && current.Descriptor.GenerationNumber == current.HighestGenerationNumber;
        if (!manifestIsImmediateSuccessor
            || !generationIsImmediateSuccessor
            || current.Descriptor.GenerationId == baseline.Descriptor.GenerationId)
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidGeneration,
                "Only the exact manifest preceding one uncommitted promotion can be restored.");
        }
    }

    private static void ValidateExpectedHash(string? value, bool allowNull)
    {
        if (value is null && allowNull)
        {
            return;
        }

        if (!DataGenerationManifestSnapshot.IsCanonicalContentHash(value))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidHash,
                "The expected manifest hash is not canonical lowercase SHA-256 text.");
        }
    }

    private async Task<FileStream> AcquireManifestLockAsync(
        CancellationToken cancellationToken)
    {
        string lockPath = _pathPolicy.ValidateStagingPath(
            _pathPolicy.CurrentManifestLockPath);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException exception) when (IsSharingViolation(exception))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsSharingViolation(IOException exception)
    {
        int nativeError = exception.HResult & 0xFFFF;
        return nativeError is 32 or 33;
    }

    private static DataGenerationStoreException CreateUnavailableException(
        string message,
        Exception exception)
    {
        return new DataGenerationStoreException(
            DataGenerationStoreError.Unavailable,
            message,
            exception);
    }

}
