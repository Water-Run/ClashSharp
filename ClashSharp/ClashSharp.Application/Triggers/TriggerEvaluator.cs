using ClashSharp.Model.Triggers;

namespace ClashSharp.ApplicationModel.Triggers;

/// <summary>Immutable typed result of context acquisition followed by pure matching.</summary>
public sealed class TriggerEvaluationDecision
{
    internal TriggerEvaluationDecision(
        TriggerContextResult contextResult,
        TriggerMatchDecision? matchDecision)
    {
        ContextResult = contextResult;
        MatchDecision = matchDecision;
    }

    /// <summary>Gets context availability and degradation details.</summary>
    public TriggerContextResult ContextResult { get; }

    /// <summary>Gets the pure match transition when context supported a decision.</summary>
    public TriggerMatchDecision? MatchDecision { get; }
}

/// <summary>Acquires minimal typed context and invokes the pure matcher for one current task record.</summary>
public sealed class TriggerEvaluator
{
    private readonly TriggerContextAcquirer _contextAcquirer;

    /// <summary>Initializes an evaluator over one asynchronous context provider.</summary>
    /// <param name="contextProvider">Host-composed provider for runtime observations.</param>
    public TriggerEvaluator(ITriggerContextProvider contextProvider)
    {
        _contextAcquirer = new TriggerContextAcquirer(
            contextProvider ?? throw new ArgumentNullException(nameof(contextProvider)));
    }

    /// <summary>Acquires only required fields and computes one immutable match transition.</summary>
    public async Task<TriggerEvaluationDecision> EvaluateAsync(
        TriggerTaskRecord task,
        TriggerEventKind eventKind,
        TriggerNotificationLevel? notificationLevel,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        TriggerContextResult contextResult = await _contextAcquirer.AcquireAsync(
            task.Definition,
            task.State,
            eventKind,
            notificationLevel,
            cancellationToken).ConfigureAwait(false);
        TriggerMatchDecision? matchDecision = null;
        if (contextResult.Status is TriggerContextStatus.Available or TriggerContextStatus.Degraded
            && contextResult.Context is TriggerEvaluationContext context)
        {
            matchDecision = TriggerMatcher.Evaluate(task.Definition, task.State, context);
        }

        return new TriggerEvaluationDecision(contextResult, matchDecision);
    }
}
