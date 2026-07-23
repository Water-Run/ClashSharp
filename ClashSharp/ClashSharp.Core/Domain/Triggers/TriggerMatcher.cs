namespace ClashSharp.Model.Triggers;

/// <summary>Pure trigger matcher implementing AND, edge, daily, and revision semantics.</summary>
public static class TriggerMatcher
{
    /// <summary>Evaluates one definition and returns its complete proposed latch transition.</summary>
    /// <param name="definition">Validated immutable trigger definition.</param>
    /// <param name="state">Latest persistent state for the same task identity.</param>
    /// <param name="context">Immutable observation context.</param>
    /// <returns>A match decision with the expected repository version and proposed next state.</returns>
    public static TriggerMatchDecision Evaluate(
        TriggerTaskDefinition definition,
        TriggerTaskState state,
        TriggerEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(context);
        if (!TriggerDefinitionValidator.Validate(definition).IsValid)
        {
            throw new ArgumentException("Trigger definition must be valid before matching.", nameof(definition));
        }

        if (!StringComparer.Ordinal.Equals(definition.Id, state.TaskId))
        {
            throw new ArgumentException("Trigger state belongs to another task.", nameof(state));
        }

        if (!definition.IsEnabled)
        {
            return new TriggerMatchDecision(
                TriggerMatchOutcome.NotMatched,
                state.Version,
                state,
                []);
        }

        TriggerTaskState effectiveState = state.TaskRevision == definition.Revision
            ? state
            : TriggerTaskState.CreateInitial(definition, state.Version, state.LastTriggeredAt);
        Dictionary<string, TriggerConditionState> nextConditions = definition.Conditions.ToDictionary(
            static condition => condition.Id,
            condition => effectiveState.ConditionStates.TryGetValue(condition.Id, out TriggerConditionState? conditionState)
                ? conditionState
                : new TriggerConditionState(),
            StringComparer.Ordinal);
        List<string> unavailableConditionIds = [];
        bool allMatched = true;
        bool anyDefinitelyFalse = false;

        foreach (TriggerCondition condition in definition.Conditions)
        {
            ConditionObservation observation = Observe(
                definition,
                condition,
                nextConditions[condition.Id],
                context);
            if (observation.IsUnavailable)
            {
                unavailableConditionIds.Add(condition.Id);
                allMatched = false;
                continue;
            }

            nextConditions[condition.Id] = observation.NextState;
            if (!observation.IsMatchCandidate)
            {
                allMatched = false;
                anyDefinitelyFalse = true;
            }
        }

        TriggerMatchOutcome outcome;
        if (allMatched)
        {
            ConsumeMatchedConditions(definition, context, nextConditions);
            outcome = TriggerMatchOutcome.Matched;
        }
        else
        {
            outcome = unavailableConditionIds.Count > 0 && !anyDefinitelyFalse
                ? TriggerMatchOutcome.InsufficientData
                : TriggerMatchOutcome.NotMatched;
        }

        TriggerTaskState nextState = new(
            definition.Id,
            definition.Revision,
            state.Version,
            nextConditions,
            effectiveState.LastTriggeredAt);
        return new TriggerMatchDecision(
            outcome,
            state.Version,
            nextState,
            unavailableConditionIds);
    }

    private static ConditionObservation Observe(
        TriggerTaskDefinition definition,
        TriggerCondition condition,
        TriggerConditionState state,
        TriggerEvaluationContext context)
    {
        return condition.Parameters switch
        {
            EventConditionParameters parameters => ConditionObservation.FromBoolean(
                context.EventKind == parameters.EventKind,
                state),
            NotificationConditionParameters parameters => ObserveNotification(parameters, state, context),
            TrafficConditionParameters parameters => ObserveTraffic(definition, parameters, state, context),
            RateConditionParameters parameters => ObserveEdge(
                parameters.Direction == TriggerTrafficDirection.Upload
                    ? context.UploadBytesPerSecond
                    : context.DownloadBytesPerSecond,
                parameters.ThresholdBytesPerSecond,
                state),
            ActiveConnectionsConditionParameters parameters => ObserveEdge(
                context.ActiveConnectionCount,
                parameters.Threshold,
                state),
            RuntimeConditionParameters parameters => ObserveRuntime(parameters, state, context),
            SystemTimeConditionParameters parameters => ConditionObservation.FromBoolean(
                context.LocalTime >= parameters.TargetTime && state.ConsumedDate != context.LocalDate,
                state),
            _ => ConditionObservation.Unavailable(state),
        };
    }

    private static ConditionObservation ObserveNotification(
        NotificationConditionParameters parameters,
        TriggerConditionState state,
        TriggerEvaluationContext context)
    {
        if (context.EventKind != TriggerEventKind.NotificationRaised)
        {
            return ConditionObservation.FromBoolean(false, state);
        }

        return context.NotificationLevel is TriggerNotificationLevel level
            ? ConditionObservation.FromBoolean(level >= parameters.MinimumLevel, state)
            : ConditionObservation.Unavailable(state);
    }

    private static ConditionObservation ObserveTraffic(
        TriggerTaskDefinition definition,
        TrafficConditionParameters parameters,
        TriggerConditionState state,
        TriggerEvaluationContext context)
    {
        long? observed = parameters.Scope switch
        {
            TriggerTrafficScope.RollingWindow when parameters.Window is TimeSpan window
                && context.RollingTrafficBytes.TryGetValue(window, out long bytes) => bytes,
            TriggerTrafficScope.CurrentSession => context.CurrentSessionTrafficBytes,
            TriggerTrafficScope.AllTime => context.AllTimeTrafficBytes,
            _ => null,
        };
        if (observed is null or < 0)
        {
            return ConditionObservation.Unavailable(state);
        }

        if (parameters.Scope == TriggerTrafficScope.AllTime)
        {
            return ConditionObservation.FromBoolean(
                observed >= parameters.ThresholdBytes && state.ConsumedRevision != definition.Revision,
                state);
        }

        return ObserveEdge(observed, parameters.ThresholdBytes, state);
    }

    private static ConditionObservation ObserveRuntime(
        RuntimeConditionParameters parameters,
        TriggerConditionState state,
        TriggerEvaluationContext context)
    {
        if (context.Runtime is not TimeSpan runtime || runtime < TimeSpan.Zero)
        {
            return ConditionObservation.Unavailable(state);
        }

        return ObserveEdge(runtime.Ticks, parameters.Threshold.Ticks, state);
    }

    private static ConditionObservation ObserveEdge<T>(
        T? observed,
        T threshold,
        TriggerConditionState state)
        where T : struct, IComparable<T>
    {
        if (observed is null || observed.Value.CompareTo(default) < 0)
        {
            return ConditionObservation.Unavailable(state);
        }

        bool isTrue = observed.Value.CompareTo(threshold) >= 0;
        if (!isTrue)
        {
            return new ConditionObservation(
                IsMatchCandidate: false,
                IsUnavailable: false,
                state with { IsArmed = true });
        }

        return ConditionObservation.FromBoolean(state.IsArmed, state);
    }

    private static void ConsumeMatchedConditions(
        TriggerTaskDefinition definition,
        TriggerEvaluationContext context,
        IDictionary<string, TriggerConditionState> nextConditions)
    {
        foreach (TriggerCondition condition in definition.Conditions)
        {
            TriggerConditionState state = nextConditions[condition.Id];
            nextConditions[condition.Id] = condition.Parameters switch
            {
                TrafficConditionParameters { Scope: TriggerTrafficScope.AllTime } =>
                    state with { ConsumedRevision = definition.Revision },
                SystemTimeConditionParameters => state with { ConsumedDate = context.LocalDate },
                TrafficConditionParameters or RateConditionParameters or
                    ActiveConnectionsConditionParameters or RuntimeConditionParameters =>
                    state with { IsArmed = false },
                _ => state,
            };
        }
    }

    private readonly record struct ConditionObservation(
        bool IsMatchCandidate,
        bool IsUnavailable,
        TriggerConditionState NextState)
    {
        public static ConditionObservation FromBoolean(bool value, TriggerConditionState state)
        {
            return new ConditionObservation(value, IsUnavailable: false, state);
        }

        public static ConditionObservation Unavailable(TriggerConditionState state)
        {
            return new ConditionObservation(IsMatchCandidate: false, IsUnavailable: true, state);
        }
    }
}
