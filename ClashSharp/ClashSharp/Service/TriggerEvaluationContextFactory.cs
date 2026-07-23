/*
 * Trigger Evaluation Context Factory
 * Bridges the temporary legacy TriggerService to asynchronous typed observations
 *
 * @author: WaterRun
 * @file: Service/TriggerEvaluationContextFactory.cs
 * @date: 2026-06-26
 */

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model;
using CoreTriggerEvaluationContext = ClashSharp.Model.Triggers.TriggerEvaluationContext;
using CoreTriggerEventKind = ClashSharp.Model.Triggers.TriggerEventKind;
using CoreTriggerNotificationLevel = ClashSharp.Model.Triggers.TriggerNotificationLevel;

namespace ClashSharp.Service;

/// <summary>Typed compatibility result for the legacy trigger evaluator.</summary>
internal sealed record TriggerEvaluationContextCreationResult(
    TriggerEvaluationContext? Context,
    string? DiagnosticCode)
{
    public static TriggerEvaluationContextCreationResult Succeeded(TriggerEvaluationContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new TriggerEvaluationContextCreationResult(context, null);
    }

    public static TriggerEvaluationContextCreationResult Unavailable(string diagnosticCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(diagnosticCode);
        return new TriggerEvaluationContextCreationResult(null, diagnosticCode);
    }
}

/// <summary>Creates legacy contexts only after every required field is asynchronously available.</summary>
internal sealed class TriggerEvaluationContextFactory
{
    private static readonly TimeSpan RecentTrafficWindow = TimeSpan.FromMinutes(5);
    private static readonly TriggerDataField[] LegacyFields =
    [
        TriggerDataField.LocalDate,
        TriggerDataField.LocalTime,
        TriggerDataField.RollingTraffic,
        TriggerDataField.CurrentSessionTraffic,
        TriggerDataField.AllTimeTraffic,
        TriggerDataField.UploadBytesPerSecond,
        TriggerDataField.DownloadBytesPerSecond,
        TriggerDataField.ActiveConnectionCount,
        TriggerDataField.Runtime,
    ];

    private readonly ITriggerContextProvider _provider;

    public TriggerEvaluationContextFactory(ITriggerContextProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public async Task<TriggerEvaluationContextCreationResult> CreateAsync(
        TriggerEventKind eventKind,
        NotificationLevel notificationLevel,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(eventKind))
        {
            throw new ArgumentOutOfRangeException(nameof(eventKind));
        }

        if (!Enum.IsDefined(notificationLevel))
        {
            throw new ArgumentOutOfRangeException(nameof(notificationLevel));
        }

        CoreTriggerEventKind coreEventKind = eventKind switch
        {
            TriggerEventKind.Periodic => CoreTriggerEventKind.Periodic,
            TriggerEventKind.AppEntered => CoreTriggerEventKind.AppEntered,
            TriggerEventKind.ProxyStarted => CoreTriggerEventKind.ProxyStarted,
            TriggerEventKind.NotificationRaised => CoreTriggerEventKind.NotificationRaised,
            _ => throw new ArgumentOutOfRangeException(nameof(eventKind)),
        };
        List<TriggerDataField> fields = [.. LegacyFields];
        CoreTriggerNotificationLevel? coreNotificationLevel = null;
        if (eventKind == TriggerEventKind.NotificationRaised)
        {
            fields.Add(TriggerDataField.NotificationLevel);
            coreNotificationLevel = notificationLevel switch
            {
                NotificationLevel.Default => CoreTriggerNotificationLevel.Default,
                NotificationLevel.CriticalOnly => CoreTriggerNotificationLevel.CriticalOnly,
                NotificationLevel.More => CoreTriggerNotificationLevel.More,
                _ => throw new ArgumentOutOfRangeException(nameof(notificationLevel)),
            };
        }

        TriggerContextResult acquired = await _provider.AcquireAsync(
            new TriggerContextRequest(
                coreEventKind,
                coreNotificationLevel,
                fields,
                [RecentTrafficWindow]),
            cancellationToken).ConfigureAwait(false);
        if (acquired.Status != TriggerContextStatus.Available
            || acquired.Context is not CoreTriggerEvaluationContext context
            || !TryMapContext(
                eventKind,
                notificationLevel,
                context,
                out TriggerEvaluationContext legacyContext))
        {
            return TriggerEvaluationContextCreationResult.Unavailable(
                acquired.DiagnosticCode ?? "trigger.context.compatibility_incomplete");
        }

        return TriggerEvaluationContextCreationResult.Succeeded(legacyContext);
    }

    private static bool TryMapContext(
        TriggerEventKind eventKind,
        NotificationLevel notificationLevel,
        CoreTriggerEvaluationContext context,
        out TriggerEvaluationContext legacyContext)
    {
        legacyContext = null!;
        if (!context.RollingTrafficBytes.TryGetValue(
                RecentTrafficWindow,
                out long rollingTraffic)
            || context.CurrentSessionTrafficBytes is not long sessionTraffic
            || context.AllTimeTrafficBytes is not long allTimeTraffic
            || context.UploadBytesPerSecond is not long uploadRate
            || context.DownloadBytesPerSecond is not long downloadRate
            || context.ActiveConnectionCount is not int activeConnections
            || context.Runtime is not TimeSpan runtime)
        {
            return false;
        }

        legacyContext = new TriggerEvaluationContext(
            eventKind,
            allTimeTraffic,
            rollingTraffic,
            runtime,
            context.LocalTime,
            notificationLevel,
            uploadRate,
            downloadRate,
            activeConnections,
            sessionTraffic);
        return true;
    }
}
