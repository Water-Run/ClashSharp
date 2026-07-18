namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Represents one host-owned producer whose work can be awaited across lifecycle transitions.</summary>
public interface IRuntimeParticipant
{
    /// <summary>Gets the stable diagnostic name of the participant.</summary>
    string Name { get; }

    /// <summary>Starts participant scheduling.</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Stops new work and awaits work already in flight.</summary>
    Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken);

    /// <summary>Restores the state observed before a failed quiescence operation.</summary>
    Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken);

    /// <summary>Permanently stops participant work before host disposal.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}
