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
    private readonly object _syncLock = new();
    private ApplicationLifetimeRequest? _acceptedRequest;

    /// <summary>Gets whether this process lifetime already accepted a winner.</summary>
    public bool HasAcceptedRequest
    {
        get
        {
            lock (_syncLock)
            {
                return _acceptedRequest is not null;
            }
        }
    }

    /// <inheritdoc />
    public bool TryRequest(ApplicationLifetimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_syncLock)
        {
            if (_acceptedRequest is not null)
            {
                return IsSameDurableHandoff(_acceptedRequest, request);
            }

            if (_channel.Writer.TryWrite(request))
            {
                _acceptedRequest = request;
                _channel.Writer.TryComplete();
                return true;
            }
        }

        return false;
    }

    /// <summary>Waits for the sole accepted process-lifetime request.</summary>
    public ValueTask<ApplicationLifetimeRequest> ReadAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }

    private static bool IsSameDurableHandoff(
        ApplicationLifetimeRequest accepted,
        ApplicationLifetimeRequest proposed)
    {
        return accepted.Handoff is not null
            && proposed.Handoff is not null
            && StringComparer.Ordinal.Equals(
                accepted.Handoff.IdempotencyKey,
                proposed.Handoff.IdempotencyKey);
    }
}
