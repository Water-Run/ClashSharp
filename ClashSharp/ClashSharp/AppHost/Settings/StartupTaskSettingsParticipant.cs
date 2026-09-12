using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Settings;

/// <summary>Applies explicit startup intent and probes Windows independently of the preferences repository.</summary>
internal sealed class StartupTaskSettingsParticipant : ISettingsApplicationParticipant
{
    private readonly StartupLaunchService _startup;
    private readonly SettingsParticipantBinding _binding;

    public StartupTaskSettingsParticipant(DataGenerationDescriptor generation, MutationAdmissionBarrier admission,
        StartupLaunchService startup)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
        _binding = new(generation, admission, SettingApplicationKind.StartupTask, SettingsRegistry.Keys.LaunchAtStartupEnabled);
    }

    public SettingApplicationKind ApplicationKind => SettingApplicationKind.StartupTask;

    public async Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _binding.Validate(request, admissionLease);
        bool enabled = await _startup.TryGetStateAsync(cancellationToken).ConfigureAwait(false) switch
        {
            StartupLaunchTaskState.Enabled => true,
            StartupLaunchTaskState.Disabled => false,
            _ => throw new InvalidOperationException("Windows startup registration could not be observed."),
        };
        return _binding.Observe(request, _ => enabled);
    }

    public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        _binding.Validate(request, admissionLease);
        return _startup.SetEnabledAsync(request.Values[SettingsRegistry.Keys.LaunchAtStartupEnabled].Get<bool>(), cancellationToken);
    }
}
