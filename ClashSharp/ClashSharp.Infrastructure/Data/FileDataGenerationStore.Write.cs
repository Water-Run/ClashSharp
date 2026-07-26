using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Infrastructure.Data;

public sealed partial class FileDataGenerationStore
{
    private async Task<DataGenerationManifestSnapshot> WriteManifestAsync(
        DataGenerationDescriptor descriptor,
        long manifestRevision,
        long highestGenerationNumber,
        CancellationToken cancellationToken)
    {
        DataGenerationManifestCodec.EncodedManifest encoded =
            DataGenerationManifestCodec.Encode(
                _pathPolicy,
                descriptor,
                manifestRevision,
                highestGenerationNumber);
        string temporaryPath = _pathPolicy.ValidateStagingPath(Path.Combine(
            _pathPolicy.DataRootPath,
            $".{DataGenerationPathPolicy.CurrentManifestFileName}.{Guid.NewGuid():N}.tmp"));
        string backupPath = _pathPolicy.ValidateStagingPath(Path.Combine(
            _pathPolicy.DataRootPath,
            $".{DataGenerationPathPolicy.CurrentManifestFileName}.previous"));

        try
        {
            await WriteAndFlushAsync(temporaryPath, encoded.Bytes, cancellationToken)
                .ConfigureAwait(false);
            await _faultInjector
                .InjectAsync(
                    DataGenerationFaultPoint.AfterTemporaryFlush,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            await _faultInjector
                .InjectAsync(
                    DataGenerationFaultPoint.BeforeManifestPromotion,
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            Promote(temporaryPath, backupPath);
            FlushPromotedManifest();

            await _faultInjector
                .InjectAsync(
                    DataGenerationFaultPoint.AfterManifestPromotion,
                    CancellationToken.None)
                .ConfigureAwait(false);
            DataGenerationManifestSnapshot loaded =
                await LoadCoreAsync(
                        manifestLockHeld: true,
                        CancellationToken.None)
                    .ConfigureAwait(false)
                ?? throw new DataGenerationStoreException(
                    DataGenerationStoreError.Corrupt,
                    "The promoted current-generation manifest is missing.");
            if (!string.Equals(loaded.ContentHash, encoded.ContentHash, StringComparison.Ordinal)
                || !loaded.Descriptor.IsSameGeneration(descriptor)
                || loaded.ManifestRevision != manifestRevision
                || loaded.HighestGenerationNumber != highestGenerationNumber)
            {
                throw new DataGenerationStoreException(
                    DataGenerationStoreError.Corrupt,
                    "The promoted current-generation manifest failed re-read verification.");
            }

            return loaded;
        }
        finally
        {
            TryDelete(temporaryPath);
            TryDelete(backupPath);
        }
    }

    private void Promote(string temporaryPath, string backupPath)
    {
        if (File.Exists(_pathPolicy.CurrentManifestPath))
        {
            TryDelete(backupPath);
            File.Replace(
                temporaryPath,
                _pathPolicy.CurrentManifestPath,
                backupPath,
                ignoreMetadataErrors: false);
            return;
        }

        File.Move(temporaryPath, _pathPolicy.CurrentManifestPath);
    }

    private void FlushPromotedManifest()
    {
        using FileStream stream = new(
            _pathPolicy.CurrentManifestPath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
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
}
