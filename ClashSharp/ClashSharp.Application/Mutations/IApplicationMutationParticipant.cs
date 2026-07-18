namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Provides idempotent stages for one participant in a journaled application mutation.</summary>
public interface IApplicationMutationParticipant
{
    /// <summary>Gets the stable participant name stored in mutation journals.</summary>
    string Name { get; }

    /// <summary>Gets opaque versioned data sufficient to compensate this participant.</summary>
    string? CompensationData { get; }

    /// <summary>Classifies the currently observed participant state.</summary>
    Task<MutationProbeState> ProbeAsync(MutationContext context, CancellationToken cancellationToken);

    /// <summary>Stages temporary participant state without publishing it.</summary>
    Task StageAsync(MutationContext context, CancellationToken cancellationToken);

    /// <summary>Applies the participant's desired external state.</summary>
    Task ApplyAsync(MutationContext context, CancellationToken cancellationToken);

    /// <summary>Verifies the participant's desired external state.</summary>
    Task VerifyAsync(MutationContext context, CancellationToken cancellationToken);

    /// <summary>Idempotently restores and verifies the participant baseline.</summary>
    Task CompensateAsync(MutationContext context, CancellationToken cancellationToken);

    /// <summary>Activates committed runtime consumers without changing the durable target.</summary>
    Task ActivateAsync(MutationContext context, CancellationToken cancellationToken);

    /// <summary>Removes committed staging or rollback material after target health is verified.</summary>
    Task CleanupAsync(MutationContext context, CancellationToken cancellationToken);
}

/// <summary>Reconstructs an operation plan from its latest validated durable journal.</summary>
public interface IMutationRecoveryPlanResolver
{
    /// <summary>Resolves only the plan described by the supplied journal.</summary>
    /// <param name="journal">Latest validated journal document.</param>
    /// <param name="cancellationToken">Cancels resolution before recovery side effects begin.</param>
    /// <returns>An idempotent recovery plan for the same operation.</returns>
    Task<MutationPlan> ResolveAsync(MutationJournal journal, CancellationToken cancellationToken);
}
