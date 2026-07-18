using System.Threading.Channels;

namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Single-winner channel owned by the outer application lifetime and shared as a non-owned sink.</summary>
public sealed class ApplicationLifetimeRequestChannel : IApplicationLifetimeRequestSink
{
    private readonly Channel<ApplicationLifetimeRequest> _channel =
        Channel.CreateBounded<ApplicationLifetimeRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private int _accepted;

    /// <inheritdoc />
    public bool TryRequest(ApplicationLifetimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (Interlocked.CompareExchange(ref _accepted, 1, 0) != 0)
        {
            return false;
        }

        if (_channel.Writer.TryWrite(request))
        {
            _channel.Writer.TryComplete();
            return true;
        }

        Volatile.Write(ref _accepted, 0);
        return false;
    }

    /// <summary>Waits for the sole accepted process-lifetime request.</summary>
    public ValueTask<ApplicationLifetimeRequest> ReadAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }
}
