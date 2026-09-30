using ClashSharp.ApplicationModel.Mutations;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Reconciles every recoverable execution outbox before normal trigger scheduling starts.</summary>
public sealed class TriggerActionReconciler
{
    private readonly ITriggerRepository _repository;
    private readonly TriggerActionExecutor _executor;
    private readonly MutationAdmissionBarrier _admissionBarrier;

    /// <summary>Initializes one startup outbox reconciler.</summary>
    public TriggerActionReconciler(
        ITriggerRepository repository,
        TriggerActionExecutor executor,
        MutationAdmissionBarrier admissionBarrier)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _admissionBarrier = admissionBarrier ?? throw new ArgumentNullException(nameof(admissionBarrier));
    }

    /// <summary>Processes recoverable executions in durable order until work is drained or blocked.</summary>
    public Task<IReadOnlyList<TriggerActionResult>> ReconcileAsync(CancellationToken cancellationToken) =>
        ReconcileCoreAsync(null, cancellationToken);

    /// <summary>Recovers outbox work under startup's existing exclusive ownership without reopening ordinary admission.</summary>
    public Task<IReadOnlyList<TriggerActionResult>> ReconcileAdmittedAsync(
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _admissionBarrier.EnsureActiveExclusiveLease(admissionLease);
        return ReconcileCoreAsync(admissionLease, cancellationToken);
    }

    private async Task<IReadOnlyList<TriggerActionResult>> ReconcileCoreAsync(
        MutationAdmissionLease? suppliedLease, CancellationToken cancellationToken)
    {
        TriggerPersistenceResult<IReadOnlyList<TriggerOutboxAction>> read =
            await _repository.ReadRecoverableActionsAsync(cancellationToken).ConfigureAwait(false);
        if (!read.IsSucceeded || read.Value is not IReadOnlyList<TriggerOutboxAction> recoverable)
        {
            throw new InvalidOperationException(
                read.Diagnostic?.Code ?? "trigger.outbox.recovery_read_unavailable");
        }

        List<TriggerActionResult> results = [];
        foreach (IGrouping<Guid, TriggerOutboxAction> executionGroup in recoverable.GroupBy(
            static action => action.ExecutionId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TriggerOutboxAction first = executionGroup.First();
            if (suppliedLease is not null)
            {
                _admissionBarrier.EnsureActiveExclusiveLease(suppliedLease);
                results.AddRange(await ReconcileExecutionAsync(executionGroup.Key, first.TaskRevision, suppliedLease, cancellationToken).ConfigureAwait(false));
            }
            else
            {
                await using MutationAdmissionLease lease = await _admissionBarrier.AcquireOrdinaryAsync(cancellationToken).ConfigureAwait(false);
                results.AddRange(await ReconcileExecutionAsync(executionGroup.Key, first.TaskRevision, lease, cancellationToken).ConfigureAwait(false));
            }
        }

        return results.AsReadOnly();
    }

    private async Task<IReadOnlyList<TriggerActionResult>> ReconcileExecutionAsync(
        Guid executionId, long revision, MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        using CancellationTokenSource admittedCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RevocationToken);
        return await _executor.ReconcileAsync(executionId, revision, lease, admittedCancellation.Token).ConfigureAwait(false);
    }
}
