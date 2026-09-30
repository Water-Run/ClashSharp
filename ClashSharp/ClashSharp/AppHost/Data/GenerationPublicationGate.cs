using System.Threading;

namespace ClashSharp.Hosting.Data;

/// <summary>Holds a generation's queued trigger events until its owner explicitly publishes execution.</summary>
internal sealed class GenerationPublicationGate
{
    private int _published;
    public bool IsPublished => Volatile.Read(ref _published) != 0;
    public bool Hold() => Interlocked.Exchange(ref _published, 0) != 0;
    public void Publish() => Interlocked.Exchange(ref _published, 1);
}
