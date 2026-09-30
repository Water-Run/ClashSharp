using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Hosting.Settings;
using ClashSharp.Model;
using ClashSharp.Model.Triggers;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Holds one generation's actual runtime services and orders their startup under exclusive ownership.</summary>
internal sealed partial class AppDataGenerationRuntime(
    AppDataGenerationRepositories repositories,
    MutationAdmissionBarrier admission,
    DataGenerationManager generations,
    ConnectionSamplingService sampling,
    NetworkTakeoverService takeover,
    TriggersSettingsParticipant triggerSettings,
    TriggerDefinitionStore definitions,
    TriggerActionReconciler outbox,
    TriggerExecutionCoordinator executions,
    ITriggerContextProvider triggerContext,
    INetworkSettingsRuntime network,
    INetworkStateObserver networkObserver,
    ProfileSubscriptionScheduler subscriptions,
    ITriggerLifecycleHandoff handoff,
    GenerationPublicationGate publication,
    GenerationExternalStateRecovery recovery,
    Guid processEpoch,
    Func<bool> exitRequested)
{
    private readonly SemaphoreSlim _startupGate = new(1, 1);
    private bool _started;
    private bool _startupPrepared;
    private string? _startupWarning;
    private bool _replacementPrepared;
    private readonly List<TriggerLifecycleHandoffIdentity> _pendingReleases = [];

    public AppDataGenerationRepositories Repositories { get; } = repositories;
    public ConnectionSamplingService Sampling { get; } = sampling;
    public NetworkTakeoverService Takeover { get; } = takeover;
    public TriggersSettingsParticipant TriggerSettings { get; } = triggerSettings;
    public TriggerDefinitionStore Definitions { get; } = definitions;
    public TriggerExecutionCoordinator Executions { get; } = executions;
    public ITriggerContextProvider TriggerContext { get; } = triggerContext;
    public INetworkStateObserver NetworkObserver { get; } = networkObserver;
    public INetworkSettingsRuntime Network { get; } = network;
    public ProfileSubscriptionScheduler Subscriptions { get; } = subscriptions;
    public bool IsExecutionPublished => publication.IsPublished;
    public GenerationExternalStateRecovery ExternalState { get; } = recovery;

    /// <summary>Applies non-network startup settings while all execution remains held for the startup policy decision.</summary>
    public async Task<StartupStepResult> PrepareStartupAdmittedAsync(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        admission.EnsureActiveExclusiveLease(lease);
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await PrepareStartupCoreAsync(lease, cancellationToken).ConfigureAwait(false); }
        finally { _startupGate.Release(); }
    }

    private async Task<StartupStepResult> PrepareStartupCoreAsync(MutationAdmissionLease lease, CancellationToken cancellationToken)
    {
        if (_startupPrepared) { return _startupWarning is null ? StartupStepResult.Succeeded() : StartupStepResult.Warning(_startupWarning); }
        SettingsAuthorityResult prepared = await Repositories.Session.PrepareStartupAdmittedAsync(Guid.NewGuid(), lease, cancellationToken).ConfigureAwait(false);
        if (!prepared.IsSucceeded) { return StartupStepResult.Fatal(prepared.Code ?? "settings.startup.prepare_failed"); }
        SettingsGenerationContext context = (SettingsGenerationContext)Repositories.GetService(typeof(SettingsGenerationContext))!;
        await TriggerSettings.Scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
        foreach (SettingApplicationKind kind in new[] { SettingApplicationKind.Internal, SettingApplicationKind.Appearance, SettingApplicationKind.StartupTask })
        {
            if (exitRequested()) { return StartupStepResult.ExitRequested(); }
            foreach (SettingsApplicationBatch batch in Repositories.Session.Snapshot.PendingApplications.Where(batch => batch.ApplicationKind == kind).ToArray())
            {
                if (CanLeaveStartupFailureForUser(kind) && batch.State == SettingsApplicationBatchState.Failed)
                {
                    _startupWarning ??= "settings.startup.retry_required";
                    continue;
                }
                SettingsAuthorityResult applied = await Repositories.Session.ApplyBatchAdmittedAsync(
                    batch.BatchId, batch.AttemptId, context.Participants[kind], SettingsApplicationPhase.Startup, lease, cancellationToken).ConfigureAwait(false);
                if (!applied.IsSucceeded)
                {
                    if (HasVerifiedFailedApplication(kind, batch, applied)) { _startupWarning ??= applied.Code; continue; }
                    return StartupStepResult.Fatal(applied.Code ?? "settings.startup.application_failed");
                }
            }
        }
        _startupPrepared = true;
        return _startupWarning is null ? StartupStepResult.Succeeded() : StartupStepResult.Warning(_startupWarning);
    }

    public Task<StartupStepResult> InitializeAdmittedAsync(MutationAdmissionLease lease, CancellationToken cancellationToken) =>
        InitializeAdmittedAsync(lease, applyNetwork: true, startupMode: null, cancellationToken);

    /// <summary>Finishes startup only after conflict checks and the configured startup policy have been resolved.</summary>
    public async Task<StartupStepResult> InitializeAdmittedAsync(MutationAdmissionLease lease, bool applyNetwork,
        ClashSharpMode? startupMode, CancellationToken cancellationToken)
    {
        admission.EnsureActiveExclusiveLease(lease);
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started) { return StartupStepResult.Succeeded(); }
            StartupStepResult prepared = await PrepareStartupCoreAsync(lease, cancellationToken).ConfigureAwait(false);
            if (prepared.Outcome is StartupStepOutcome.Fatal or StartupStepOutcome.ExitRequested) { return prepared; }
            if (applyNetwork && startupMode is { } mode)
            {
                SettingDefinition definition = SettingsRegistry.Default.Get(SettingsRegistry.Keys.CurrentMode.Value);
                SettingsAuthorityResult selected = await Repositories.Session.ChangeAdmittedAsync(
                    [new(definition.Key, definition.NormalizeValue(mode).Value!)], Guid.NewGuid(), lease, cancellationToken).ConfigureAwait(false);
                if (!selected.IsSucceeded) { return StartupStepResult.Fatal(selected.Code ?? "settings.startup.mode_failed"); }
            }
            SettingsGenerationContext context = (SettingsGenerationContext)Repositories.GetService(typeof(SettingsGenerationContext))!;
            SettingApplicationKind[] order = [SettingApplicationKind.Network, SettingApplicationKind.Sampling, SettingApplicationKind.Triggers];
            string? warning = applyNetwork ? _startupWarning : "startup-network-conflicts-pending";
            foreach (SettingApplicationKind kind in order)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (exitRequested()) { return StartupStepResult.ExitRequested(); }
                if (kind == SettingApplicationKind.Network && !applyNetwork) { continue; }
                if (kind == SettingApplicationKind.Triggers)
                {
                    TriggerPersistenceResult<TriggerDefinitionCatalog> catalog = await Definitions.ReadAsync(cancellationToken).ConfigureAwait(false);
                    if (!catalog.IsSucceeded) { return StartupStepResult.Fatal(catalog.Diagnostic?.Code ?? "trigger.startup.catalog_unavailable"); }
                    // No settings-session or command lock is held here. Recovery actions may
                    // call the same settings authority using the already-owned startup lease.
                    var recovered = await outbox.ReconcileAdmittedAsync(lease, cancellationToken).ConfigureAwait(false);
                    foreach (var result in recovered.Where(result => result.FinalState == TriggerOutboxState.HandedOff))
                    {
                        _pendingReleases.Add(new(result.Action.ExecutionId, result.Action.ActionIndex, processEpoch));
                    }
                    if (exitRequested() || recovered.Any(result => result.FinalState == TriggerOutboxState.HandedOff))
                    {
                        return StartupStepResult.ExitRequested();
                    }
                    warning ??= recovered.FirstOrDefault(result => result.DiagnosticCode is not null)?.DiagnosticCode;
                    await TriggerSettings.Scheduler.StartAsync(cancellationToken).ConfigureAwait(false);
                }
                // An outbox action may have changed or completed an earlier batch. Always
                // resolve the latest pending identities before applying this participant.
                SettingsApplicationBatch[] batches = Repositories.Session.Snapshot.PendingApplications
                    .Where(batch => batch.ApplicationKind == kind).ToArray();
                foreach (SettingsApplicationBatch batch in batches)
                {
                    if (CanLeaveStartupFailureForUser(kind) && batch.State == SettingsApplicationBatchState.Failed)
                    {
                        warning ??= "settings.startup.retry_required";
                        continue;
                    }
                    SettingsAuthorityResult applied = await Repositories.Session.ApplyBatchAdmittedAsync(
                        batch.BatchId, batch.AttemptId, context.Participants[kind], SettingsApplicationPhase.Startup, lease, cancellationToken).ConfigureAwait(false);
                    if (!applied.IsSucceeded)
                    {
                        if (HasVerifiedFailedApplication(kind, batch, applied)) { warning ??= applied.Code; continue; }
                        return StartupStepResult.Fatal(applied.Code ?? "settings.startup.application_failed");
                    }
                }
            }
            _started = true;
            return warning is null ? StartupStepResult.Succeeded() : StartupStepResult.Warning(warning);
        }
        finally { _startupGate.Release(); }
    }

    /// <summary>Acknowledges recovered exit handoffs only after startup has released its exclusive mutation lease.</summary>
    public async Task AcknowledgeStartupReleaseAsync(CancellationToken cancellationToken)
    {
        await _startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            while (_pendingReleases.Count > 0)
            {
                await handoff.AcknowledgeReleaseAsync(_pendingReleases[0], cancellationToken).ConfigureAwait(false);
                _pendingReleases.RemoveAt(0);
            }
            if (_started && !exitRequested()) { PublishTriggerProcessing(); }
        }
        finally { _startupGate.Release(); }
    }

    private void PublishTriggerProcessing()
    {
        if (admission.State != MutationAdmissionState.Open)
        {
            throw new InvalidOperationException("Trigger execution cannot be published while ordinary mutations remain closed.");
        }
        publication.Publish();
        TriggerSettings.Scheduler.NotifyProcessingAvailabilityChanged();
    }

    private static bool CanLeaveStartupFailureForUser(SettingApplicationKind kind) =>
        kind is SettingApplicationKind.Appearance or SettingApplicationKind.StartupTask or SettingApplicationKind.Network or SettingApplicationKind.Sampling;

    private static bool HasVerifiedFailedApplication(SettingApplicationKind kind, SettingsApplicationBatch attempted, SettingsAuthorityResult result) =>
        CanLeaveStartupFailureForUser(kind) && result.Status == SettingsAuthorityStatus.ApplicationFailed
        && result.Envelope?.PendingApplications.Any(batch => batch.BatchId == attempted.BatchId && batch.AttemptId == attempted.AttemptId
            && batch.State == SettingsApplicationBatchState.Failed) == true;
}
