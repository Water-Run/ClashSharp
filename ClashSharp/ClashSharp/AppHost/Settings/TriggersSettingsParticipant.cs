using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Settings;

/// <summary>Owns a generation's scheduler and the installed enablement consumed by its evaluation loop.</summary>
/// <remarks>The host initializes trigger storage before starting <see cref="Scheduler"/> and admitting settings application.</remarks>
internal sealed class TriggersSettingsParticipant : ISettingsApplicationParticipant, IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TriggerSettingsState _settings;
    private readonly SettingsParticipantBinding _binding;
    private bool _disposed;

    public TriggersSettingsParticipant(DataGenerationDescriptor generation, MutationAdmissionBarrier admission,
        TriggerSettingsState settings,
        ITriggerSchedulerEventSource events, ITriggerSchedulerClock clock, ITriggerSchedulerEvaluator evaluator,
        ITriggerLifecycleHandoff handoff, Action<SupervisorHealth>? healthChanged = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (!_settings.Generation.IsSameGeneration(generation))
        {
            throw new ArgumentException("The trigger consumers must share the participant's generation.", nameof(settings));
        }
        _binding = new(generation, admission, SettingApplicationKind.Triggers,
            SettingsRegistry.Keys.TriggersEnabled, SettingsRegistry.Keys.TriggerNotificationsEnabled);
        Scheduler = new(_settings, events, clock, evaluator, handoff, healthChanged);
    }

    /// <summary>Gets the same scheduler that host startup initializes and runtime lifecycle operations drain.</summary>
    public TriggerScheduler Scheduler { get; }

    public SettingApplicationKind ApplicationKind => SettingApplicationKind.Triggers;

    public async Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _binding.Validate(request, admissionLease);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _binding.Validate(request, admissionLease);
            EnsureSchedulerReady();
            TriggerSettingsState.Configuration installed = _settings.Read();
            return _binding.Observe(request, key => key == SettingsRegistry.Keys.TriggersEnabled
                ? installed.Enabled : installed.NotificationsEnabled);
        }
        finally { _gate.Release(); }
    }

    public async Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        _binding.Validate(request, admissionLease);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _binding.Validate(request, admissionLease);
            cancellationToken.ThrowIfCancellationRequested();
            EnsureSchedulerReady();
            // Once draining begins, this owner finishes the transition even if the page cancels.
            // Quiescence drains queued evaluations. The resumed loop continues retrying release
            // acknowledgements even when new trigger evaluation is disabled.
            QuiescedState prior = await Scheduler.QuiesceAsync(CancellationToken.None).ConfigureAwait(false);
            if (!prior.WasRunning) { throw new InvalidOperationException("The trigger scheduler could not be quiesced for settings application."); }
            TriggerSettingsState.Configuration installed = _settings.Read();
            _settings.Install(new(
                request.Values.TryGetValue(SettingsRegistry.Keys.TriggersEnabled, out SettingValue? enabled)
                    ? enabled.Get<bool>() : installed.Enabled,
                request.Values.TryGetValue(SettingsRegistry.Keys.TriggerNotificationsEnabled, out SettingValue? notifications)
                    ? notifications.Get<bool>() : installed.NotificationsEnabled));
            await Scheduler.ResumeAsync(prior, CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) { return; }
            _disposed = true;
            try { await Scheduler.StopAsync(CancellationToken.None).ConfigureAwait(false); }
            finally { _settings.Retire(); }
        }
        finally { _gate.Release(); }
    }

    private void EnsureSchedulerReady()
    {
        if (!Scheduler.IsRunning || !Scheduler.IsAcceptingEvents)
        {
            throw new InvalidOperationException("The trigger scheduler is not ready to observe or apply settings.");
        }
    }

}
