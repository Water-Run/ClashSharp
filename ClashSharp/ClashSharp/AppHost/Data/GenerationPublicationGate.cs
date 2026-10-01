using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Hosting.Data;

/// <summary>Holds queued trigger events and subscription work until the generation owner publishes execution.</summary>
internal sealed class GenerationPublicationGate
{
    private readonly object _sync = new();
    private TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _published;
    public bool IsPublished => Volatile.Read(ref _published) != 0;

    public bool Hold()
    {
        lock (_sync)
        {
            bool wasPublished = Interlocked.Exchange(ref _published, 0) != 0;
            if (wasPublished) { _release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
            return wasPublished;
        }
    }

    public void Publish()
    {
        TaskCompletionSource release;
        lock (_sync)
        {
            Volatile.Write(ref _published, 1);
            release = _release;
        }
        release.TrySetResult();
    }

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task release;
            lock (_sync)
            {
                if (_published != 0) { return; }
                release = _release.Task;
            }
            await release.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
