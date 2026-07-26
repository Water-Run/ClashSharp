using ClashSharp.ApplicationModel.Data;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Data;

public sealed partial class FileDataGenerationStore
{
    private async Task<DataGenerationManifestSnapshot?> LoadCoreAsync(
        bool manifestLockHeld,
        CancellationToken cancellationToken)
    {
        _pathPolicy.EnsureLayout();
        FileStream? stabilityLock = null;
        if (!File.Exists(_pathPolicy.CurrentManifestPath))
        {
            if (manifestLockHeld)
            {
                return null;
            }

            stabilityLock = await AcquireManifestLockAsync(cancellationToken)
                .ConfigureAwait(false);
            if (!File.Exists(_pathPolicy.CurrentManifestPath))
            {
                await stabilityLock.DisposeAsync().ConfigureAwait(false);
                return null;
            }
        }

        try
        {
            SafeFileHandle? openedHandle = null;
            const int maximumOpenAttempts = 64;
            for (int attempt = 1; attempt <= maximumOpenAttempts; attempt++)
            {
                try
                {
                    openedHandle = ReparseSafeFile.OpenRead(
                        _pathPolicy.CurrentManifestPath,
                        FileShare.ReadWrite | FileShare.Delete,
                        asynchronous: true);
                    break;
                }
                catch (Exception exception) when (
                    attempt < maximumOpenAttempts
                    && ReparseSafeFile.IsTransientOpenFailure(exception))
                {
                    if (!manifestLockHeld && stabilityLock is null)
                    {
                        stabilityLock = await AcquireManifestLockAsync(cancellationToken)
                            .ConfigureAwait(false);
                        if (!File.Exists(_pathPolicy.CurrentManifestPath))
                        {
                            return null;
                        }
                    }
                    else
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(1), cancellationToken)
                            .ConfigureAwait(false);
                    }
                }
            }

            using SafeFileHandle handle = openedHandle
                ?? throw new DataGenerationStoreException(
                    DataGenerationStoreError.Unavailable,
                    "The current-generation manifest could not be opened consistently.");
            _pathPolicy.EnsureLayout();
            long manifestLength = RandomAccess.GetLength(handle);
            if (manifestLength is <= 0 or > MaximumManifestBytes)
            {
                throw new DataGenerationStoreException(
                    DataGenerationStoreError.Corrupt,
                    "The current-generation manifest has an invalid size.");
            }

            byte[] bytes = new byte[checked((int)manifestLength)];
            int bytesRead = 0;
            while (bytesRead < bytes.Length)
            {
                int read = await RandomAccess
                    .ReadAsync(
                        handle,
                        bytes.AsMemory(bytesRead),
                        bytesRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    throw new DataGenerationStoreException(
                        DataGenerationStoreError.Corrupt,
                        "The current-generation manifest ended unexpectedly.");
                }

                bytesRead += read;
            }

            DataGenerationManifestSnapshot snapshot =
                DataGenerationManifestCodec.Decode(bytes, _pathPolicy);
            _pathPolicy.ValidateDescriptor(snapshot.Descriptor);
            return snapshot;
        }
        finally
        {
            if (stabilityLock is not null)
            {
                await stabilityLock.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}
