using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Applies one settings batch through an admitted runtime boundary and independently probes its effect.</summary>
/// <remarks>Implementations use explicit request values, never write settings authority, and own all tasks until completion.</remarks>
public interface ISettingsApplicationParticipant
{
    /// <summary>Gets the application kind this participant can observe and apply.</summary>
    SettingApplicationKind ApplicationKind { get; }

    /// <summary>Observes actual effective values without changing runtime or preference state.</summary>
    /// <param name="request">Immutable generation and attempt to observe.</param>
    /// <param name="admissionLease">Caller-owned lease retained for the complete command.</param>
    /// <param name="cancellationToken">Observation cancellation; failures must not fabricate desired values.</param>
    Task<SettingsApplicationObservation> ProbeAsync(
        SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken);

    /// <summary>Converges idempotently toward explicit desired values; successful return still requires an independent probe.</summary>
    /// <param name="request">Immutable target and companion settings.</param>
    /// <param name="admissionLease">Existing authority; implementations must not reacquire ordinary admission.</param>
    /// <param name="cancellationToken">Owner-controlled token; a page cancellation cannot abandon a started effect.</param>
    Task ApplyAsync(SettingsApplicationRequest request, MutationAdmissionLease admissionLease, CancellationToken cancellationToken);
}
