using System.Threading.Channels;

namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>
/// Single-active-request channel owned by the outer application lifetime and shared as a non-owned sink.
/// </summary>
/// <remarks>
/// The runner owns an unconfirmed durable checkpoint until its request confirms terminal persistence.
/// When bounded automatic retries end, this channel releases the active slot for an ordinary exit but
/// reserves that durable identity. Only that identity may reclaim durable processing, so a successful
/// <see cref="TryRequest"/> never strands a different durable handoff behind the runner checkpoint.
/// </remarks>
public sealed class ApplicationLifetimeRequestChannel : IApplicationLifetimeRequestSink
{
    private const int MaximumAutomaticDurableRetries = 3;
    private static readonly TimeSpan InitialAutomaticDurableRetryDelay =
        TimeSpan.FromMilliseconds(25);
    private readonly Channel<ApplicationLifetimeRequest> _channel =
        Channel.CreateBounded<ApplicationLifetimeRequest>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
        });
    private readonly object _syncLock = new();
    private readonly HashSet<string> _terminalDurableHandoffKeys = new(StringComparer.Ordinal);
    private ApplicationLifetimeRequest? _acceptedRequest;
    private ApplicationLifetimeRequest? _pendingDurableRequest;
    private string? _reservedUnconfirmedDurableHandoffKey;
    private int _automaticDurableRetryCount;
    private bool _automaticDurableRetryScheduled;

    /// <summary>Gets whether one process-lifetime request is currently awaiting completion.</summary>
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
            if (request.Handoff is not null
                && _terminalDurableHandoffKeys.Contains(request.Handoff.IdempotencyKey))
            {
                return true;
            }

            if (request.Handoff is not null
                && _reservedUnconfirmedDurableHandoffKey is not null
                && !StringComparer.Ordinal.Equals(
                    _reservedUnconfirmedDurableHandoffKey,
                    request.Handoff.IdempotencyKey))
            {
                return false;
            }

            if (_acceptedRequest is not null)
            {
                if (IsSameDurableHandoff(_acceptedRequest, request))
                {
                    return true;
                }

                if (request.Handoff is not null)
                {
                    if (_acceptedRequest.Handoff is not null)
                    {
                        return false;
                    }

                    if (_pendingDurableRequest is null)
                    {
                        _pendingDurableRequest = request;
                        return true;
                    }

                    return IsSameDurableHandoff(_pendingDurableRequest, request);
                }

                return false;
            }

            if (_channel.Writer.TryWrite(request))
            {
                _acceptedRequest = request;
                _automaticDurableRetryCount = 0;
                return true;
            }
        }

        return false;
    }

    /// <summary>Waits for the next accepted process-lifetime request.</summary>
    public ValueTask<ApplicationLifetimeRequest> ReadAsync(CancellationToken cancellationToken)
    {
        return _channel.Reader.ReadAsync(cancellationToken);
    }

    /// <summary>
    /// Releases the exact accepted request after outer shutdown fails so a later user request can retry.
    /// </summary>
    /// <remarks>
    /// A durable identity remains idempotently acknowledged without being re-enqueued only when its
    /// persistence handoff confirmed a terminal succeeded, failed, or uncertain outcome.
    /// </remarks>
    /// <returns>True when <paramref name="request"/> was the active request and was released.</returns>
    public bool TryReleaseFailedRequest(ApplicationLifetimeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_syncLock)
        {
            if (!ReferenceEquals(_acceptedRequest, request))
            {
                return false;
            }

            if (request.Handoff is not null)
            {
                if (request.TerminalStatePersistence
                    != ApplicationLifetimeTerminalStatePersistence.Confirmed)
                {
                    return false;
                }

                _terminalDurableHandoffKeys.Add(request.Handoff.IdempotencyKey);
                ClearUnconfirmedDurableReservation(request.Handoff.IdempotencyKey);
            }

            ReleaseAcceptedRequest();
            return true;
        }
    }

    /// <summary>
    /// Requeues the exact active durable request after a transient unconfirmed callback failure.
    /// </summary>
    /// <remarks>
    /// The channel retains active ownership during an exponential delay and permits at most three
    /// automatic retries. Exhaustion releases the active slot but reserves the unconfirmed durable
    /// identity for explicit producer reconciliation. An ordinary exit can still take the slot;
    /// a different durable identity cannot.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/> when the exact request was requeued; otherwise <see langword="false"/>.
    /// </returns>
    public async ValueTask<bool> RetryFailedRequestAsync(
        ApplicationLifetimeRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        int retryNumber;
        lock (_syncLock)
        {
            if (!ReferenceEquals(_acceptedRequest, request)
                || _automaticDurableRetryScheduled)
            {
                return false;
            }

            if (request.Handoff is null
                || request.TerminalStatePersistence
                    == ApplicationLifetimeTerminalStatePersistence.Confirmed)
            {
                if (request.Handoff is not null)
                {
                    _terminalDurableHandoffKeys.Add(request.Handoff.IdempotencyKey);
                    ClearUnconfirmedDurableReservation(request.Handoff.IdempotencyKey);
                }

                ReleaseAcceptedRequest();
                return false;
            }

            if (_automaticDurableRetryCount >= MaximumAutomaticDurableRetries)
            {
                ReserveUnconfirmedDurableRequest(request);
                ReleaseAcceptedRequest();
                return false;
            }

            retryNumber = ++_automaticDurableRetryCount;
            _automaticDurableRetryScheduled = true;
        }

        TimeSpan delay = InitialAutomaticDurableRetryDelay * Math.Pow(2, retryNumber - 1);
        try
        {
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_syncLock)
            {
                if (ReferenceEquals(_acceptedRequest, request))
                {
                    ReserveUnconfirmedDurableRequest(request);
                    ReleaseAcceptedRequest();
                }
            }

            throw;
        }

        lock (_syncLock)
        {
            _automaticDurableRetryScheduled = false;
            if (!ReferenceEquals(_acceptedRequest, request))
            {
                return false;
            }

            if (request.TerminalStatePersistence
                == ApplicationLifetimeTerminalStatePersistence.Confirmed)
            {
                _terminalDurableHandoffKeys.Add(request.Handoff.IdempotencyKey);
                ClearUnconfirmedDurableReservation(request.Handoff.IdempotencyKey);
                ReleaseAcceptedRequest();
                return false;
            }

            if (_channel.Writer.TryWrite(request))
            {
                return true;
            }

            ReserveUnconfirmedDurableRequest(request);
            ReleaseAcceptedRequest();
            return false;
        }
    }

    private void ReleaseAcceptedRequest()
    {
        _acceptedRequest = null;
        _automaticDurableRetryCount = 0;
        _automaticDurableRetryScheduled = false;
        PromotePendingDurableRequest();
    }

    private void ReserveUnconfirmedDurableRequest(ApplicationLifetimeRequest request)
    {
        IApplicationLifetimeHandoff handoff = request.Handoff
            ?? throw new ArgumentException(
                "Only a durable request can reserve a handoff identity.",
                nameof(request));
        if (_reservedUnconfirmedDurableHandoffKey is not null
            && !StringComparer.Ordinal.Equals(
                _reservedUnconfirmedDurableHandoffKey,
                handoff.IdempotencyKey))
        {
            throw new InvalidOperationException(
                "A different unconfirmed durable lifetime request already owns the process checkpoint.");
        }

        _reservedUnconfirmedDurableHandoffKey = handoff.IdempotencyKey;
    }

    private void ClearUnconfirmedDurableReservation(string idempotencyKey)
    {
        if (StringComparer.Ordinal.Equals(
            _reservedUnconfirmedDurableHandoffKey,
            idempotencyKey))
        {
            _reservedUnconfirmedDurableHandoffKey = null;
        }
    }

    private void PromotePendingDurableRequest()
    {
        if (_pendingDurableRequest is not ApplicationLifetimeRequest pending)
        {
            return;
        }

        _pendingDurableRequest = null;
        if (pending.Handoff is not null
            && _terminalDurableHandoffKeys.Contains(pending.Handoff.IdempotencyKey))
        {
            return;
        }

        if (_channel.Writer.TryWrite(pending))
        {
            _acceptedRequest = pending;
            return;
        }

        _pendingDurableRequest = pending;
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
