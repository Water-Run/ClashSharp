namespace ClashSharp.ApplicationModel.Mutations;

/// <summary>Describes one fully planned, journal-ready application mutation.</summary>
public sealed class MutationPlan
{
    /// <summary>Initializes an immutable mutation plan.</summary>
    /// <param name="baselineHash">Hash of the verified baseline.</param>
    /// <param name="desiredHash">Hash of the desired target.</param>
    /// <param name="participants">Ordered idempotent mutation participants.</param>
    /// <param name="validateAsync">Performs read-only plan validation before journal creation.</param>
    /// <param name="promoteTargetAsync">Atomically promotes the durable desired target.</param>
    /// <param name="restoreBaselineAsync">Restores durable target material before the commit marker.</param>
    /// <param name="verifyDesiredTargetAsync">Verifies the durable desired target.</param>
    /// <param name="verifyBaselineAsync">Verifies the restored durable baseline.</param>
    public MutationPlan(
        string baselineHash,
        string desiredHash,
        IReadOnlyList<IApplicationMutationParticipant> participants,
        Func<CancellationToken, Task> validateAsync,
        Func<MutationContext, CancellationToken, Task> promoteTargetAsync,
        Func<MutationContext, CancellationToken, Task> restoreBaselineAsync,
        Func<MutationContext, CancellationToken, Task> verifyDesiredTargetAsync,
        Func<MutationContext, CancellationToken, Task> verifyBaselineAsync)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineHash);
        ArgumentException.ThrowIfNullOrWhiteSpace(desiredHash);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(validateAsync);
        ArgumentNullException.ThrowIfNull(promoteTargetAsync);
        ArgumentNullException.ThrowIfNull(restoreBaselineAsync);
        ArgumentNullException.ThrowIfNull(verifyDesiredTargetAsync);
        ArgumentNullException.ThrowIfNull(verifyBaselineAsync);

        IApplicationMutationParticipant[] participantArray = participants.ToArray();
        if (participantArray.Any(participant => participant is null || string.IsNullOrWhiteSpace(participant.Name)))
        {
            throw new ArgumentException("Mutation participants must have stable non-empty names.", nameof(participants));
        }

        if (participantArray.Select(participant => participant.Name).Distinct(StringComparer.Ordinal).Count()
            != participantArray.Length)
        {
            throw new ArgumentException("Mutation participant names must be unique within a plan.", nameof(participants));
        }

        BaselineHash = baselineHash;
        DesiredHash = desiredHash;
        Participants = participantArray;
        ValidateAsync = validateAsync;
        PromoteTargetAsync = promoteTargetAsync;
        RestoreBaselineAsync = restoreBaselineAsync;
        VerifyDesiredTargetAsync = verifyDesiredTargetAsync;
        VerifyBaselineAsync = verifyBaselineAsync;
    }

    /// <summary>Gets the verified baseline hash.</summary>
    public string BaselineHash { get; }

    /// <summary>Gets the planned desired-target hash.</summary>
    public string DesiredHash { get; }

    /// <summary>Gets the ordered immutable participant list.</summary>
    public IReadOnlyList<IApplicationMutationParticipant> Participants { get; }

    internal Func<CancellationToken, Task> ValidateAsync { get; }

    internal Func<MutationContext, CancellationToken, Task> PromoteTargetAsync { get; }

    internal Func<MutationContext, CancellationToken, Task> RestoreBaselineAsync { get; }

    internal Func<MutationContext, CancellationToken, Task> VerifyDesiredTargetAsync { get; }

    internal Func<MutationContext, CancellationToken, Task> VerifyBaselineAsync { get; }
}
