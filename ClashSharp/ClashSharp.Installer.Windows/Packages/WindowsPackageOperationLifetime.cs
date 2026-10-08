using Windows.Foundation;

namespace ClashSharp.Installer.Windows.Packages;

/// <summary>Owns a projected package operation until its native completion is observed.</summary>
internal static class WindowsPackageOperationLifetime
{
    internal static async Task<TResult> AwaitAsync<TResult, TProgress>(
        IAsyncOperationWithProgress<TResult, TProgress> operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        try
        {
            return await operation.AsTask().ConfigureAwait(false);
        }
        finally
        {
            // The Task bridge does not retain the projected operation when no cancellation
            // token is supplied. Keep its native completion subscription alive through every
            // terminal outcome; an abandoned projection can leave a committed deployment unacknowledged.
            GC.KeepAlive(operation);
        }
    }
}
