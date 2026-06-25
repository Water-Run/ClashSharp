/*
 * Trigger Evaluation Context Factory
 * Builds runtime context snapshots for trigger event evaluation
 *
 * @author: WaterRun
 * @file: Service/TriggerEvaluationContextFactory.cs
 * @date: 2026-06-26
 */

using System;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Creates trigger evaluation contexts from current runtime state.</summary>
internal static class TriggerEvaluationContextFactory
{
    private static readonly DateTimeOffset StartedAt = DateTimeOffset.Now;

    public static TriggerEvaluationContext Create(
        TriggerEventKind eventKind,
        NotificationLevel notificationLevel = NotificationLevel.Default)
    {
        TrafficStatisticsSummary summary = LogStorageService.Instance.GetTrafficStatisticsSummary();
        return new TriggerEvaluationContext(
            eventKind,
            summary.TotalUploadBytes + summary.TotalDownloadBytes,
            WindowTrafficBytes: 0,
            DateTimeOffset.Now - StartedAt,
            TimeOnly.FromDateTime(DateTime.Now),
            notificationLevel);
    }
}
