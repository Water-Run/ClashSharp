using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Network;

/// <summary>Routes every network transition through the process-wide mutation owner.</summary>
public sealed class NetworkStateCoordinator
{
    /// <summary>Stable journal operation type used by network recovery.</summary>
    public const string OperationType = "network.transition";

    private readonly IApplicationMutationCoordinator _mutations;
    private readonly INetworkStateAdapter _adapter;
    private readonly INetworkStateCommitter _committer;

    /// <summary>Initializes the network transition use case.</summary>
    /// <param name="mutations">Sole top-level mutation owner.</param>
    /// <param name="adapter">Staged external network adapter.</param>
    /// <param name="committer">Durable desired/applied-state committer.</param>
    public NetworkStateCoordinator(
        IApplicationMutationCoordinator mutations,
        INetworkStateAdapter adapter,
        INetworkStateCommitter committer)
    {
        _mutations = mutations ?? throw new ArgumentNullException(nameof(mutations));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
    }

    /// <summary>Plans and executes one verified network transition as a top-level mutation.</summary>
    /// <param name="intent">Validated desired network state.</param>
    /// <param name="cancellationToken">Cancels admission and pre-side-effect planning.</param>
    /// <returns>A durable mutation outcome containing verified effective state on success.</returns>
    public Task<MutationResult<NetworkTransitionResult>> ApplyAsync(
        NetworkIntent intent,
        CancellationToken cancellationToken)
    {
        NetworkIntent.Validate(intent);
        NetworkMutationParticipant? participant = null;
        MutationRequest request = MutationRequest.Create(OperationType);
        return _mutations.ExecuteAsync(
            request,
            async (context, token) =>
            {
                NetworkPlan plan = await PlanAsync(context, intent, token).ConfigureAwait(false);
                participant = new NetworkMutationParticipant(_adapter, plan);
                return NetworkMutationPlanFactory.Create(participant, _committer);
            },
            (context, token) =>
            {
                _mutations.EnsureContextOwnership(context);
                token.ThrowIfCancellationRequested();
                return Task.FromResult(
                    participant?.CreateResult()
                    ?? throw new InvalidOperationException("The network mutation participant was not created."));
            },
            cancellationToken);
    }

    /// <summary>Builds a network plan only while the owning mutation context is active.</summary>
    /// <param name="context">Active context issued by the owning mutation coordinator.</param>
    /// <param name="intent">Validated desired network state.</param>
    /// <param name="cancellationToken">Cancels non-mutating planning.</param>
    /// <returns>An immutable journal-ready network plan.</returns>
    public async Task<NetworkPlan> PlanAsync(
        MutationContext context,
        NetworkIntent intent,
        CancellationToken cancellationToken)
    {
        _mutations.EnsureContextOwnership(context);
        NetworkIntent.Validate(intent);
        NetworkPlan plan = await _adapter.PlanAsync(intent, cancellationToken).ConfigureAwait(false);
        NetworkMutationPlanFactory.Validate(plan, intent);
        return plan;
    }
}

/// <summary>Reconstructs retained network plans without depending on the public top-level coordinator.</summary>
public sealed class NetworkMutationRecoveryPlanResolver : IMutationRecoveryPlanResolver
{
    private readonly INetworkStateAdapter _adapter;
    private readonly INetworkStateCommitter _committer;

    /// <summary>Initializes network recovery plan reconstruction.</summary>
    /// <param name="adapter">Staged external network adapter.</param>
    /// <param name="committer">Durable desired/applied-state committer.</param>
    public NetworkMutationRecoveryPlanResolver(
        INetworkStateAdapter adapter,
        INetworkStateCommitter committer)
    {
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _committer = committer ?? throw new ArgumentNullException(nameof(committer));
    }

    /// <inheritdoc />
    public async Task<MutationPlan> ResolveAsync(MutationJournal journal, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(journal);
        if (!string.Equals(journal.OperationType, NetworkStateCoordinator.OperationType, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"Operation type '{journal.OperationType}' is not a network transition.");
        }

        NetworkPlan plan = await _adapter.RestorePlanAsync(journal, cancellationToken).ConfigureAwait(false);
        NetworkMutationPlanFactory.Validate(plan, plan.Intent);
        NetworkMutationParticipant participant = new(_adapter, plan);
        return NetworkMutationPlanFactory.Create(participant, _committer);
    }
}

internal static class NetworkMutationPlanFactory
{
    public static MutationPlan Create(
        NetworkMutationParticipant participant,
        INetworkStateCommitter committer)
    {
        NetworkPlan plan = participant.Plan;
        return new MutationPlan(
            plan.BaselineHash,
            plan.DesiredHash,
            [participant],
            token => participant.ValidateAsync(token),
            (_, token) => committer.PromoteDesiredAsync(plan, token),
            (_, token) => committer.RestoreBaselineAsync(plan, token),
            (_, token) => committer.VerifyDesiredAsync(plan, token),
            (_, token) => committer.VerifyBaselineAsync(plan, token));
    }

    public static void Validate(NetworkPlan plan, NetworkIntent intent)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Intent != intent
            || plan.Baseline is null
            || plan.Desired is null
            || !plan.Baseline.IsKnown
            || !plan.Desired.IsKnown
            || string.IsNullOrWhiteSpace(plan.Baseline.StateHash)
            || string.IsNullOrWhiteSpace(plan.Desired.StateHash)
            || string.IsNullOrWhiteSpace(plan.BaselineHash)
            || string.IsNullOrWhiteSpace(plan.DesiredHash)
            || string.IsNullOrWhiteSpace(plan.CompensationData))
        {
            throw new InvalidOperationException("The network adapter returned an incomplete or mismatched plan.");
        }
    }
}

internal sealed class NetworkMutationParticipant(
    INetworkStateAdapter adapter,
    NetworkPlan plan) : IApplicationMutationParticipant
{
    private NetworkStateSnapshot? _verifiedState;

    public string Name => "network-state";

    public string? CompensationData => plan.CompensationData;

    public NetworkPlan Plan => plan;

    public async Task<MutationProbeState> ProbeAsync(
        MutationContext context,
        CancellationToken cancellationToken)
    {
        NetworkStateSnapshot state = await adapter.ProbeAsync(plan, cancellationToken).ConfigureAwait(false);
        if (!state.IsKnown)
        {
            return MutationProbeState.Unknown;
        }

        if (string.Equals(state.StateHash, plan.Baseline.StateHash, StringComparison.Ordinal))
        {
            return MutationProbeState.Baseline;
        }

        if (string.Equals(state.StateHash, plan.Desired.StateHash, StringComparison.Ordinal))
        {
            _verifiedState = state;
            return MutationProbeState.Desired;
        }

        return MutationProbeState.Partial;
    }

    public Task ValidateAsync(CancellationToken cancellationToken)
    {
        return adapter.ValidateAsync(plan, cancellationToken);
    }

    public Task StageAsync(MutationContext context, CancellationToken cancellationToken)
    {
        return adapter.StageAsync(plan, cancellationToken);
    }

    public Task ApplyAsync(MutationContext context, CancellationToken cancellationToken)
    {
        return adapter.ApplyAsync(plan, cancellationToken);
    }

    public async Task VerifyAsync(MutationContext context, CancellationToken cancellationToken)
    {
        MutationProbeState state = await ProbeAsync(context, cancellationToken).ConfigureAwait(false);
        if (state != MutationProbeState.Desired)
        {
            throw new InvalidOperationException($"The network transition did not verify its desired state; observed '{state}'.");
        }
    }

    public Task CompensateAsync(MutationContext context, CancellationToken cancellationToken)
    {
        _verifiedState = null;
        return adapter.CompensateAsync(plan, cancellationToken);
    }

    public Task ActivateAsync(MutationContext context, CancellationToken cancellationToken)
    {
        return adapter.ActivateAsync(plan, cancellationToken);
    }

    public Task CleanupAsync(MutationContext context, CancellationToken cancellationToken)
    {
        return adapter.CleanupAsync(plan, cancellationToken);
    }

    public NetworkTransitionResult CreateResult()
    {
        NetworkStateSnapshot state = _verifiedState
            ?? throw new InvalidOperationException("The network transition has no verified desired state.");
        return new NetworkTransitionResult(
            state.Mode,
            state.CoreRunning,
            state.SystemProxyEnabled,
            state.TransparentProxyEnabled,
            state.MixedPort,
            state.StateHash);
    }
}
