/*
 * Trigger Task Normalizer
 * Deduplicates trigger conditions and resolves mutually exclusive actions
 *
 * @author: WaterRun
 * @file: Service/TriggerTaskNormalizer.cs
 * @date: 2026-06-29
 */

using System;
using System.Collections.Generic;
using System.Linq;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Result returned after normalizing one trigger task.</summary>
internal sealed record TriggerTaskNormalizationResult(
    TriggerTask Task,
    bool WasChanged,
    string ChangeSummary);

/// <summary>Normalizes trigger task condition and action lists before storage and execution.</summary>
internal static class TriggerTaskNormalizer
{
    public static TriggerTaskNormalizationResult Normalize(TriggerTask task)
    {
        ArgumentNullException.ThrowIfNull(task);

        IReadOnlyList<TriggerCondition> normalizedConditions = MergeDuplicateConditions(task.Conditions);
        IReadOnlyList<TriggerAction> normalizedActions = NormalizeActions(task.Actions);
        TriggerTask normalizedTask = new(
            task.Id,
            task.Name,
            task.IsEnabled,
            normalizedConditions,
            normalizedActions,
            task.LastTriggeredAt);

        bool conditionsChanged = !task.Conditions.SequenceEqual(normalizedConditions);
        bool actionsChanged = !task.Actions.SequenceEqual(normalizedActions);
        if (!conditionsChanged && !actionsChanged)
        {
            return new TriggerTaskNormalizationResult(normalizedTask, false, string.Empty);
        }

        List<string> changes = [];
        if (conditionsChanged)
        {
            changes.Add("merged duplicate conditions");
        }

        if (actionsChanged)
        {
            changes.Add("merged duplicate actions");
        }

        return new TriggerTaskNormalizationResult(normalizedTask, true, string.Join("; ", changes));
    }

    private static IReadOnlyList<TriggerCondition> MergeDuplicateConditions(IReadOnlyList<TriggerCondition> conditions)
    {
        List<TriggerCondition> normalized = [];
        HashSet<TriggerCondition> seen = [];
        foreach (TriggerCondition condition in conditions)
        {
            if (seen.Add(condition))
            {
                normalized.Add(condition);
            }
        }

        return normalized;
    }

    private static IReadOnlyList<TriggerAction> NormalizeActions(IReadOnlyList<TriggerAction> actions)
    {
        List<TriggerAction> terminalActions = [];
        List<TriggerAction> normalized = [];
        Dictionary<TriggerActionKind, TriggerAction> lastExclusiveActions = [];
        HashSet<TriggerActionKind> emittedExclusiveKinds = [];

        foreach (TriggerAction action in actions)
        {
            if (action.Kind == TriggerActionKind.ExitApplication)
            {
                terminalActions.Clear();
                terminalActions.Add(action);
                continue;
            }

            if (IsLastWinsAction(action.Kind))
            {
                lastExclusiveActions[action.Kind] = action;
                continue;
            }

            if (!normalized.Contains(action))
            {
                normalized.Add(action);
            }
        }

        List<TriggerAction> result = [];
        foreach (TriggerAction action in actions)
        {
            if (!IsLastWinsAction(action.Kind) || emittedExclusiveKinds.Contains(action.Kind))
            {
                continue;
            }

            result.Add(lastExclusiveActions[action.Kind]);
            emittedExclusiveKinds.Add(action.Kind);
        }

        foreach (TriggerAction action in normalized)
        {
            if (!result.Contains(action))
            {
                result.Add(action);
            }
        }

        result.AddRange(terminalActions);
        return result;
    }

    private static bool IsLastWinsAction(TriggerActionKind kind)
    {
        return kind is TriggerActionKind.SetLaunchAtStartup
            or TriggerActionKind.SetTransparentProxy
            or TriggerActionKind.SetConnectionSampling
            or TriggerActionKind.SwitchProxyMode
            or TriggerActionKind.SendNotification;
    }
}
