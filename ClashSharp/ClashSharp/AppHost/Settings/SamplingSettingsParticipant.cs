using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.Service;
using ClashSharp.Settings;

namespace ClashSharp.Hosting.Settings;

/// <summary>Installs a complete sampling pair without writing preferences or reacquiring mutation admission.</summary>
internal sealed class SamplingSettingsParticipant : ISettingsApplicationParticipant
{
    private readonly ConnectionSamplingService _sampling;
    private readonly SettingsParticipantBinding _binding;

    public SamplingSettingsParticipant(DataGenerationDescriptor generation, MutationAdmissionBarrier admission,
        ConnectionSamplingService sampling)
    {
        _sampling = sampling ?? throw new ArgumentNullException(nameof(sampling));
        _binding = new(generation, admission, SettingApplicationKind.Sampling,
            SettingsRegistry.Keys.ConnectionSamplingEnabled, SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds);
    }

    public SettingApplicationKind ApplicationKind => SettingApplicationKind.Sampling;

    public async Task<SettingsApplicationObservation> ProbeAsync(SettingsApplicationRequest request,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken)
    {
        _binding.Validate(request, admissionLease);
        ConnectionSamplingSettings observed = await _sampling.ReadConfigurationAsync(cancellationToken).ConfigureAwait(false);
        return _binding.Observe(request, key => key == SettingsRegistry.Keys.ConnectionSamplingEnabled
            ? (object)observed.Enabled : observed.IntervalSeconds);
    }

    public Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease,
        CancellationToken cancellationToken)
    {
        _binding.Validate(request, admissionLease);
        ConnectionSamplingSettings target = new(
            request.Envelope.Desired[SettingsRegistry.Keys.ConnectionSamplingEnabled].Value.Get<bool>(),
            request.Envelope.Desired[SettingsRegistry.Keys.ConnectionSamplingIntervalSeconds].Value.Get<int>());
        return _sampling.ApplyConfigurationAsync(target, cancellationToken);
    }
}
