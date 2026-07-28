using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Hosting.Startup;
using ClashSharp.Infrastructure.Triggers;
using CoreTriggerEventKind = ClashSharp.Model.Triggers.TriggerEventKind;
using CoreTriggerNotificationLevel = ClashSharp.Model.Triggers.TriggerNotificationLevel;
using LegacyTriggerNotificationLevel = ClashSharp.Model.NotificationLevel;

namespace ClashSharp.Service;

/// <summary>Reads trigger enablement from the host-owned settings service.</summary>
internal sealed class TriggerSchedulerSettingsAdapter(AppSettingsService settings) : ITriggerSchedulerSettings
{
    private readonly AppSettingsService _settings = settings
        ?? throw new ArgumentNullException(nameof(settings));

    public bool IsEnabled => _settings.TriggersEnabled;
}

/// <summary>Maps legacy runtime publishers to typed application scheduler events without detached work.</summary>
internal sealed class TriggerSchedulerEventSourceAdapter : ITriggerSchedulerEventSource
{
    private readonly object _syncLock = new();
    private readonly ITriggerRuntimeEventSource _source;
    private EventHandler<TriggerSchedulerEvent>? _eventRaised;
    private bool _attached;

    public TriggerSchedulerEventSourceAdapter(ITriggerRuntimeEventSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public event EventHandler<TriggerSchedulerEvent>? EventRaised
    {
        add
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_syncLock)
            {
                _eventRaised += value;
                if (!_attached)
                {
                    _source.RuntimeEventRaised += OnRuntimeEventRaised;
                    _attached = true;
                }
            }
        }
        remove
        {
            ArgumentNullException.ThrowIfNull(value);
            lock (_syncLock)
            {
                _eventRaised -= value;
                if (_attached && _eventRaised is null)
                {
                    _source.RuntimeEventRaised -= OnRuntimeEventRaised;
                    _attached = false;
                }
            }
        }
    }

    private void OnRuntimeEventRaised(object? sender, TriggerRuntimeEvent triggerEvent)
    {
        TriggerSchedulerEvent mapped = triggerEvent.EventKind switch
        {
            CoreTriggerEventKind.Periodic => new TriggerSchedulerEvent(CoreTriggerEventKind.Periodic),
            CoreTriggerEventKind.AppEntered => new TriggerSchedulerEvent(CoreTriggerEventKind.AppEntered),
            CoreTriggerEventKind.ProxyStarted => new TriggerSchedulerEvent(CoreTriggerEventKind.ProxyStarted),
            CoreTriggerEventKind.NotificationRaised => new TriggerSchedulerEvent(
                CoreTriggerEventKind.NotificationRaised,
                MapNotificationLevel(triggerEvent.NotificationLevel)),
            _ => throw new InvalidDataException("The runtime event source published an undefined trigger event."),
        };
        EventHandler<TriggerSchedulerEvent>? handlers;
        lock (_syncLock)
        {
            handlers = _eventRaised;
        }

        handlers?.Invoke(this, mapped);
    }

    private static CoreTriggerNotificationLevel MapNotificationLevel(
        LegacyTriggerNotificationLevel level)
    {
        return level switch
        {
            LegacyTriggerNotificationLevel.Default => CoreTriggerNotificationLevel.Default,
            LegacyTriggerNotificationLevel.CriticalOnly => CoreTriggerNotificationLevel.CriticalOnly,
            LegacyTriggerNotificationLevel.More => CoreTriggerNotificationLevel.More,
            _ => throw new InvalidDataException("The runtime event source published an undefined notification level."),
        };
    }
}

/// <summary>Produces awaited periodic ticks at one fixed positive interval.</summary>
internal sealed class SystemTriggerSchedulerClock : ITriggerSchedulerClock
{
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _interval;

    public SystemTriggerSchedulerClock(TimeProvider timeProvider, TimeSpan interval)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        if (interval <= TimeSpan.Zero || interval > TimeSpan.FromDays(1))
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
    }

    public DateTimeOffset UtcNow => _timeProvider.GetUtcNow();

    public Task WaitForNextTickAsync(CancellationToken cancellationToken) =>
        Task.Delay(_interval, _timeProvider, cancellationToken);
}

/// <summary>Opens/migrates durable trigger state and reconciles its outbox before scheduling.</summary>
internal sealed class TriggerStartupInitializer(
    TriggerMigrationCoordinator migration,
    TriggerActionReconciler reconciler,
    ITriggerDefinitionStore definitionStore) : ITriggerStartupInitializer
{
    private readonly TriggerMigrationCoordinator _migration = migration
        ?? throw new ArgumentNullException(nameof(migration));
    private readonly TriggerActionReconciler _reconciler = reconciler
        ?? throw new ArgumentNullException(nameof(reconciler));
    private readonly ITriggerDefinitionStore _definitionStore = definitionStore
        ?? throw new ArgumentNullException(nameof(definitionStore));

    public async Task<StartupStepResult> InitializeAsync(CancellationToken cancellationToken)
    {
        TriggerMigrationResult migrationResult = await _migration
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        if (migrationResult.Status == TriggerMigrationStatus.Unavailable)
        {
            return StartupStepResult.Fatal(
                migrationResult.Diagnostics.FirstOrDefault()?.Code
                    ?? "trigger.startup.migration_unavailable");
        }

        TriggerPersistenceResult<TriggerDefinitionCatalog> catalog = await _definitionStore
            .ReadAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!catalog.IsSucceeded)
        {
            return StartupStepResult.Fatal(
                catalog.Diagnostic?.Code ?? "trigger.startup.catalog_unavailable");
        }

        IReadOnlyList<TriggerActionResult> reconciled;
        try
        {
            reconciled = await _reconciler
                .ReconcileAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception))
        {
            return StartupStepResult.Fatal(
                SupervisorFailureClassifier.Classify(exception) switch
                {
                    "supervisor.sqlite" => "trigger.startup.reconciliation_sqlite",
                    "supervisor.io" => "trigger.startup.reconciliation_io",
                    _ => "trigger.startup.reconciliation_failed",
                });
        }

        string? warning = migrationResult.Diagnostics.FirstOrDefault()?.Code
            ?? reconciled.FirstOrDefault(result => result.DiagnosticCode is not null)?.DiagnosticCode;
        return warning is null
            ? StartupStepResult.Succeeded()
            : StartupStepResult.Warning(warning);
    }
}

/// <summary>Writes only unhealthy scheduler transitions to the existing diagnostic log.</summary>
internal sealed class TriggerSchedulerHealthLogAdapter
{
    public void Report(SupervisorHealth health)
    {
        ArgumentNullException.ThrowIfNull(health);
        if (health.State is not (SupervisorHealthState.Retrying or SupervisorHealthState.Degraded))
        {
            return;
        }

        LogStorageService.Instance.AppendLog(
            "Warning",
            "Trigger",
            LocalizationService.Instance.GetString("Triggers.Log.RuntimeEventFailed"),
            health.ErrorCode);
    }
}
