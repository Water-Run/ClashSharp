namespace ClashSharp.ApplicationModel.Settings;

public sealed partial class SettingsAuthoritySession
{
    private readonly object _lifetimeLock = new();
    private Task? _disposal;
    private bool _closing;

    /// <summary>Rejects later commands and drains any started effect and final save before releasing this generation's projection.</summary>
    public ValueTask DisposeAsync()
    {
        lock (_lifetimeLock)
        {
            Volatile.Write(ref _closing, true);
            _disposal ??= DrainAsync();
            return new(_disposal);
        }
    }

    private async Task DrainAsync()
    {
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Volatile.Write(ref _snapshot, null);
        }
        finally
        {
            // Queued callers still need to acquire and reject themselves. The managed
            // semaphore stays valid until their continuations have observed closure.
            _operationGate.Release();
        }
    }

    private void ThrowIfClosing() => ObjectDisposedException.ThrowIf(Volatile.Read(ref _closing), this);
}
