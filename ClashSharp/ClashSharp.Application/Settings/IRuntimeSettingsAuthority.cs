using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Executes runtime actions with fresh effect observation, including an unchanged desired choice.</summary>
public interface IRuntimeSettingsAuthority : ISettingsAuthority
{
    /// <summary>Retains mutation admission and its generation through commit, fresh observation and any required application.</summary>
    Task<SettingsAuthorityResult> ApplyRuntimeChangesAsync(
        IEnumerable<SettingValueChange> changes, Guid commandId, CancellationToken cancellationToken);

    /// <summary>Uses the caller's existing admission through the complete runtime command without reacquiring it.</summary>
    Task<SettingsAuthorityResult> ApplyRuntimeChangesAdmittedAsync(
        IEnumerable<SettingValueChange> changes, Guid commandId,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken);
}
