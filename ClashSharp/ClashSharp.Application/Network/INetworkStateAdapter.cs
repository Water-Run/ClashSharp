using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Network;

/// <summary>Plans, probes, and applies staged mihomo, service, TUN, and Windows proxy state.</summary>
public interface INetworkStateAdapter
{
    /// <summary>Builds a non-mutating plan from the latest observed baseline.</summary>
    Task<NetworkPlan> PlanAsync(NetworkIntent intent, CancellationToken cancellationToken);

    /// <summary>Reconstructs the exact plan authorized by a retained journal.</summary>
    Task<NetworkPlan> RestorePlanAsync(MutationJournal journal, CancellationToken cancellationToken);

    /// <summary>Rejects conflicts or insufficient compensation data before staging.</summary>
    Task ValidateAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Writes only temporary configuration required by the plan.</summary>
    Task StageAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Applies mihomo, service, TUN, and Windows proxy side effects.</summary>
    Task ApplyAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Probes the complete current external state.</summary>
    Task<NetworkStateSnapshot> ProbeAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Idempotently restores the verified external baseline.</summary>
    Task CompensateAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Activates committed runtime consumers.</summary>
    Task ActivateAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Removes staging and rollback material after committed verification.</summary>
    Task CleanupAsync(NetworkPlan plan, CancellationToken cancellationToken);
}

/// <summary>Promotes and verifies durable desired/applied network state without applying external network effects.</summary>
public interface INetworkStateCommitter
{
    /// <summary>Atomically promotes the desired durable target.</summary>
    Task PromoteDesiredAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Restores the durable baseline before the commit marker.</summary>
    Task RestoreBaselineAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Verifies the promoted durable desired target.</summary>
    Task VerifyDesiredAsync(NetworkPlan plan, CancellationToken cancellationToken);

    /// <summary>Verifies the restored durable baseline.</summary>
    Task VerifyBaselineAsync(NetworkPlan plan, CancellationToken cancellationToken);
}
