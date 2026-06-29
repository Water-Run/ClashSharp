/*
 * Trigger Task Normalizer Tests
 * Verifies trigger condition and action normalization before storage and execution
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/Services/TriggerTaskNormalizerTests.cs
 * @date: 2026-06-29
 */

using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Unit tests for trigger task normalization.</summary>
public sealed class TriggerTaskNormalizerTests
{
    [Fact]
    public void Normalize_MergesDuplicateConditions()
    {
        TriggerTask task = new(
            "duplicate-conditions",
            "Duplicate conditions",
            true,
            [
                new TriggerCondition(TriggerConditionKind.Runtime, 60),
                new TriggerCondition(TriggerConditionKind.Runtime, 60),
            ],
            [new TriggerAction(TriggerActionKind.SendNotification, "run")]);

        TriggerTaskNormalizationResult result = TriggerTaskNormalizer.Normalize(task);

        TriggerCondition condition = Assert.Single(result.Task.Conditions);
        Assert.Equal(TriggerConditionKind.Runtime, condition.Kind);
        Assert.True(result.WasChanged);
        Assert.Contains("merged", result.ChangeSummary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Normalize_KeepsLastMutuallyExclusiveAction()
    {
        TriggerTask task = new(
            "exclusive-actions",
            "Exclusive actions",
            true,
            [new TriggerCondition(TriggerConditionKind.AppEntered)],
            [
                new TriggerAction(TriggerActionKind.SwitchProxyMode, ClashSharpMode.RuleTakeover.ToString()),
                new TriggerAction(TriggerActionKind.SwitchProxyMode, ClashSharpMode.FullTakeover.ToString()),
                new TriggerAction(TriggerActionKind.SetTransparentProxy, bool.FalseString),
                new TriggerAction(TriggerActionKind.SetTransparentProxy, bool.TrueString),
            ]);

        TriggerTaskNormalizationResult result = TriggerTaskNormalizer.Normalize(task);

        Assert.Equal(
            [
                new TriggerAction(TriggerActionKind.SwitchProxyMode, ClashSharpMode.FullTakeover.ToString()),
                new TriggerAction(TriggerActionKind.SetTransparentProxy, bool.TrueString),
            ],
            result.Task.Actions);
        Assert.True(result.WasChanged);
    }

    [Fact]
    public void Normalize_KeepsLastNotificationAndMovesExitLast()
    {
        TriggerTask task = new(
            "terminal-action",
            "Terminal action",
            true,
            [new TriggerCondition(TriggerConditionKind.AppEntered)],
            [
                new TriggerAction(TriggerActionKind.SendNotification, "first"),
                new TriggerAction(TriggerActionKind.ExitApplication),
                new TriggerAction(TriggerActionKind.SendNotification, "second"),
                new TriggerAction(TriggerActionKind.CloseConnections),
            ]);

        TriggerTaskNormalizationResult result = TriggerTaskNormalizer.Normalize(task);

        Assert.Equal(
            [
                new TriggerAction(TriggerActionKind.SendNotification, "second"),
                new TriggerAction(TriggerActionKind.CloseConnections),
                new TriggerAction(TriggerActionKind.ExitApplication),
            ],
            result.Task.Actions);
    }

    [Fact]
    public void Normalize_UnchangedTask_ReturnsNoChangeSummary()
    {
        TriggerTask task = new(
            "unchanged",
            "Unchanged",
            true,
            [new TriggerCondition(TriggerConditionKind.AppEntered)],
            [new TriggerAction(TriggerActionKind.SendNotification, "run")]);

        TriggerTaskNormalizationResult result = TriggerTaskNormalizer.Normalize(task);

        Assert.False(result.WasChanged);
        Assert.Equal(string.Empty, result.ChangeSummary);
    }
}
