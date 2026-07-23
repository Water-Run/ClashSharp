using System.Collections.ObjectModel;

namespace ClashSharp.Model.Triggers;

/// <summary>One stable trigger-definition validation error.</summary>
/// <param name="Code">Machine-stable error code.</param>
/// <param name="Path">Definition path associated with the error.</param>
public sealed record TriggerValidationError(string Code, string Path);

/// <summary>Immutable validation result for one trigger definition.</summary>
public sealed class TriggerDefinitionValidationResult
{
    internal TriggerDefinitionValidationResult(IEnumerable<TriggerValidationError> errors)
    {
        Errors = Array.AsReadOnly(errors.ToArray());
    }

    /// <summary>Gets whether the definition satisfies every domain invariant.</summary>
    public bool IsValid => Errors.Count == 0;

    /// <summary>Gets all validation errors in deterministic traversal order.</summary>
    public ReadOnlyCollection<TriggerValidationError> Errors { get; }
}

/// <summary>Validates typed trigger definitions before persistence or execution.</summary>
public static class TriggerDefinitionValidator
{
    /// <summary>Validates one immutable definition without changing it.</summary>
    /// <param name="definition">Definition to validate.</param>
    /// <returns>All stable validation errors.</returns>
    public static TriggerDefinitionValidationResult Validate(TriggerTaskDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        List<TriggerValidationError> errors = [];

        AddWhen(string.IsNullOrWhiteSpace(definition.Id), "trigger.id.required", "id", errors);
        AddWhen(definition.Revision <= 0, "trigger.revision.invalid", "revision", errors);
        AddWhen(string.IsNullOrWhiteSpace(definition.Name), "trigger.name.required", "name", errors);
        AddWhen(definition.Conditions.Count == 0, "trigger.conditions.required", "conditions", errors);
        AddWhen(definition.Actions.Count == 0, "trigger.actions.required", "actions", errors);

        HashSet<string> conditionIds = new(StringComparer.Ordinal);
        for (int index = 0; index < definition.Conditions.Count; index++)
        {
            ValidateCondition(definition.Conditions[index], index, conditionIds, errors);
        }

        for (int index = 0; index < definition.Actions.Count; index++)
        {
            ValidateAction(definition.Actions[index], index, definition.Actions.Count, errors);
        }

        return new TriggerDefinitionValidationResult(errors);
    }

    private static void ValidateCondition(
        TriggerCondition? condition,
        int index,
        ISet<string> conditionIds,
        ICollection<TriggerValidationError> errors)
    {
        string path = $"conditions[{index}]";
        if (condition is null)
        {
            errors.Add(new TriggerValidationError("trigger.condition.required", path));
            return;
        }

        if (string.IsNullOrWhiteSpace(condition.Id))
        {
            errors.Add(new TriggerValidationError("trigger.condition.id.required", path + ".id"));
        }
        else if (!conditionIds.Add(condition.Id))
        {
            errors.Add(new TriggerValidationError("trigger.condition.id.duplicate", path + ".id"));
        }

        if (!Enum.IsDefined(condition.Kind))
        {
            errors.Add(new TriggerValidationError("trigger.condition.kind.undefined", path + ".kind"));
            return;
        }

        switch (condition.Kind)
        {
            case TriggerConditionKind.Event when condition.Parameters is EventConditionParameters parameters:
                ValidateEvent(parameters, path, errors);
                break;
            case TriggerConditionKind.Notification when condition.Parameters is NotificationConditionParameters parameters:
                if (!Enum.IsDefined(parameters.MinimumLevel))
                {
                    errors.Add(new TriggerValidationError(
                        "trigger.condition.notification.level.undefined",
                        path + ".parameters.minimumLevel"));
                }

                break;
            case TriggerConditionKind.Traffic when condition.Parameters is TrafficConditionParameters parameters:
                ValidateTraffic(parameters, path, errors);
                break;
            case TriggerConditionKind.Rate when condition.Parameters is RateConditionParameters parameters:
                if (!Enum.IsDefined(parameters.Direction))
                {
                    errors.Add(new TriggerValidationError(
                        "trigger.condition.rate.direction.undefined",
                        path + ".parameters.direction"));
                }

                AddPositiveThreshold(parameters.ThresholdBytesPerSecond, path, errors);
                break;
            case TriggerConditionKind.ActiveConnections when condition.Parameters is ActiveConnectionsConditionParameters parameters:
                AddPositiveThreshold(parameters.Threshold, path, errors);
                break;
            case TriggerConditionKind.Runtime when condition.Parameters is RuntimeConditionParameters parameters:
                AddWhen(
                    parameters.Threshold <= TimeSpan.Zero,
                    "trigger.condition.threshold.invalid",
                    path + ".parameters.threshold",
                    errors);
                break;
            case TriggerConditionKind.SystemTime when condition.Parameters is SystemTimeConditionParameters:
                break;
            default:
                errors.Add(new TriggerValidationError(
                    "trigger.condition.parameters.mismatch",
                    path + ".parameters"));
                break;
        }
    }

    private static void ValidateEvent(
        EventConditionParameters parameters,
        string path,
        ICollection<TriggerValidationError> errors)
    {
        if (!Enum.IsDefined(parameters.EventKind))
        {
            errors.Add(new TriggerValidationError(
                "trigger.condition.event.undefined",
                path + ".parameters.eventKind"));
        }
        else if (parameters.EventKind is not (TriggerEventKind.AppEntered or TriggerEventKind.ProxyStarted))
        {
            errors.Add(new TriggerValidationError(
                "trigger.condition.event.invalid",
                path + ".parameters.eventKind"));
        }
    }

    private static void ValidateTraffic(
        TrafficConditionParameters parameters,
        string path,
        ICollection<TriggerValidationError> errors)
    {
        if (!Enum.IsDefined(parameters.Scope))
        {
            errors.Add(new TriggerValidationError(
                "trigger.condition.traffic.scope.undefined",
                path + ".parameters.scope"));
        }

        AddPositiveThreshold(parameters.ThresholdBytes, path, errors);
        if (parameters.Scope == TriggerTrafficScope.RollingWindow)
        {
            AddWhen(
                parameters.Window is null || parameters.Window <= TimeSpan.Zero,
                "trigger.condition.window.invalid",
                path + ".parameters.window",
                errors);
        }
        else if (parameters.Window is not null)
        {
            errors.Add(new TriggerValidationError(
                "trigger.condition.window.unexpected",
                path + ".parameters.window"));
        }
    }

    private static void ValidateAction(
        TriggerAction? action,
        int index,
        int actionCount,
        ICollection<TriggerValidationError> errors)
    {
        string path = $"actions[{index}]";
        if (action is null)
        {
            errors.Add(new TriggerValidationError("trigger.action.required", path));
            return;
        }

        if (!Enum.IsDefined(action.Kind))
        {
            errors.Add(new TriggerValidationError("trigger.action.kind.undefined", path + ".kind"));
            return;
        }

        bool parametersMatch = action.Kind switch
        {
            TriggerActionKind.CloseConnections or TriggerActionKind.ExitApplication =>
                action.Parameters is NoActionParameters,
            TriggerActionKind.SetLaunchAtStartup or
                TriggerActionKind.SetTransparentProxy or
                TriggerActionKind.SetConnectionSampling => action.Parameters is BooleanActionParameters,
            TriggerActionKind.SwitchProxyMode => action.Parameters is ProxyModeActionParameters,
            TriggerActionKind.SendNotification => action.Parameters is NotificationActionParameters,
            _ => false,
        };
        if (!parametersMatch)
        {
            errors.Add(new TriggerValidationError(
                "trigger.action.parameters.mismatch",
                path + ".parameters"));
            return;
        }

        if (action is { Kind: TriggerActionKind.SwitchProxyMode, Parameters: ProxyModeActionParameters modeParameters }
            && (!Enum.IsDefined(modeParameters.Mode) || modeParameters.Mode == ClashSharpMode.Faulted))
        {
            errors.Add(new TriggerValidationError("trigger.action.mode.undefined", path + ".parameters.mode"));
        }

        if (action is { Kind: TriggerActionKind.SendNotification, Parameters: NotificationActionParameters notificationParameters }
            && string.IsNullOrWhiteSpace(notificationParameters.Message))
        {
            errors.Add(new TriggerValidationError(
                "trigger.action.notification.message.required",
                path + ".parameters.message"));
        }

        if (action.Kind == TriggerActionKind.ExitApplication && index != actionCount - 1)
        {
            errors.Add(new TriggerValidationError("trigger.action.exit.must_be_final", path));
        }
    }

    private static void AddPositiveThreshold(
        long threshold,
        string path,
        ICollection<TriggerValidationError> errors)
    {
        AddWhen(
            threshold <= 0,
            "trigger.condition.threshold.invalid",
            path + ".parameters.threshold",
            errors);
    }

    private static void AddWhen(
        bool condition,
        string code,
        string path,
        ICollection<TriggerValidationError> errors)
    {
        if (condition)
        {
            errors.Add(new TriggerValidationError(code, path));
        }
    }
}
