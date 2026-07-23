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
    public async Task<IReadOnlyList<TriggerActionResult>> ReconcileAsync(
        CancellationToken cancellationToken)
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
            MutationAdmissionLease admissionLease = await _admissionBarrier.AcquireOrdinaryAsync(
                cancellationToken).ConfigureAwait(false);
            await using (admissionLease.ConfigureAwait(false))
            using (CancellationTokenSource admittedCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    admissionLease.RevocationToken))
            {
                IReadOnlyList<TriggerActionResult> executionResults = await _executor.ReconcileAsync(
                    executionGroup.Key,
                    first.TaskRevision,
                    admissionLease,
                    admittedCancellation.Token).ConfigureAwait(false);
                results.AddRange(executionResults);
            }
        }

        return results.AsReadOnly();
    }
}
