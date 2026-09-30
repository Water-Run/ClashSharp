using ClashSharp.ApplicationModel.Network;

namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Tracks runtime services actually constructed by the host without resolving later startup dependencies.</summary>
/// <remarks>Registration does not transfer disposal ownership from the host. Startup must settle before host shutdown.</remarks>
public sealed class RuntimeLifetimeRegistry
{
    private readonly object _syncLock = new();
    private readonly List<(IRuntimeParticipant Participant, int Order)> _participants = [];
    private RuntimeShutdownBinding? _network;
    private bool _shutdownCaptureActive;
    private bool _shutdownCommitted;

    /// <summary>Records an existing producer and returns it to its owning service factory.</summary>
    /// <typeparam name="TParticipant">Concrete host-owned producer.</typeparam>
    /// <param name="participant">Already constructed producer; this method never starts it.</param>
    /// <param name="order">Quiescence order, independent of dependency construction order.</param>
    public TParticipant RegisterParticipant<TParticipant>(TParticipant participant, int order = 0) where TParticipant : class, IRuntimeParticipant
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentException.ThrowIfNullOrWhiteSpace(participant.Name);
        lock (_syncLock)
        {
            EnsureRegistrationOpen();
            if (_participants.Any(existing => ReferenceEquals(existing.Participant, participant))) { return participant; }
            if (_participants.Any(existing => StringComparer.Ordinal.Equals(existing.Participant.Name, participant.Name)))
            {
                throw new ArgumentException("Runtime participant names must be unique.", nameof(participant));
            }
            _participants.Add((participant, order));
            return participant;
        }
    }

    /// <summary>Installs network cleanup before the first startup step that may recover or change network state.</summary>
    /// <param name="network">Already constructed coordinator owned by the host.</param>
    /// <param name="shutdownIntentFactory">Reads the configured exit policy only when shutdown actually needs an intent.</param>
    public void RegisterNetwork(IRuntimeShutdownNetworkCoordinator network, Func<NetworkIntent> shutdownIntentFactory)
    {
        ArgumentNullException.ThrowIfNull(network);
        ArgumentNullException.ThrowIfNull(shutdownIntentFactory);
        lock (_syncLock)
        {
            EnsureRegistrationOpen();
            if (_network is not null) { throw new InvalidOperationException("Network shutdown is already registered for this host."); }
            _network = new(network, shutdownIntentFactory);
        }
    }

    internal ShutdownCapture CaptureForShutdown()
    {
        lock (_syncLock)
        {
            if (_shutdownCaptureActive) { throw new InvalidOperationException("Another shutdown owns the runtime registrations."); }
            RuntimeLifetimeSnapshot snapshot = new(_participants.OrderBy(entry => entry.Order).Select(entry => entry.Participant).ToArray(), _network);
            _shutdownCaptureActive = true;
            return new(this, snapshot);
        }
    }

    private void EnsureRegistrationOpen()
    {
        if (_shutdownCaptureActive || _shutdownCommitted)
        {
            throw new InvalidOperationException("Runtime services cannot be registered after shutdown begins.");
        }
    }

    private void ReleaseCapture(bool committed)
    {
        lock (_syncLock)
        {
            _shutdownCommitted |= committed;
            _shutdownCaptureActive = false;
        }
    }

    internal sealed class ShutdownCapture(RuntimeLifetimeRegistry owner, RuntimeLifetimeSnapshot snapshot) : IDisposable
    {
        private RuntimeLifetimeRegistry? _owner = owner;
        private bool _committed;
        public RuntimeLifetimeSnapshot Snapshot { get; } = snapshot;
        public void Commit() => _committed = true;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseCapture(_committed);
    }
}

internal sealed record RuntimeShutdownBinding(IRuntimeShutdownNetworkCoordinator Coordinator, Func<NetworkIntent> CreateIntent);

internal sealed record RuntimeLifetimeSnapshot(IReadOnlyList<IRuntimeParticipant> Participants, RuntimeShutdownBinding? Network);
