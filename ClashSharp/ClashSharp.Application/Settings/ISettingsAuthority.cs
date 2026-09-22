using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Provides generation-pinned snapshots and complete asynchronous desired/application commands to production consumers.</summary>
public interface ISettingsAuthority
{
    /// <summary>Captures current immutable state without storage I/O or retaining a repository reference.</summary>
    SettingsAuthoritySnapshot CaptureSnapshot();

    /// <summary>Opens the current initialized authority under ordinary admission.</summary>
    /// <param name="cancellationToken">Cancels acquisition and storage observation.</param>
    Task<SettingsAuthorityResult> OpenAsync(CancellationToken cancellationToken);

    /// <summary>Commits a complete desired change set and verifies affected application batches under one generation pin.</summary>
    /// <remarks>Changes that quiesce settings-producing triggers acquire exclusive admission before the command gate.</remarks>
    /// <param name="changes">Canonical typed desired changes copied before waiting.</param>
    /// <param name="transactionId">Stable identity of the requested change set.</param>
    /// <param name="cancellationToken">Cancels waiting and work before the desired publication boundary.</param>
    Task<SettingsAuthorityResult> ApplyChangesAsync(
        IEnumerable<SettingValueChange> changes, Guid transactionId, CancellationToken cancellationToken);

    /// <summary>Performs the complete change and application using an existing caller-owned admission lease.</summary>
    /// <remarks>Trigger settings require an exclusive lease; an ordinary lease is rejected before desired publication.</remarks>
    /// <param name="changes">Canonical typed desired changes copied before waiting.</param>
    /// <param name="transactionId">Stable identity of the change set.</param>
    /// <param name="admissionLease">Active lease retained by the caller until the complete command finishes.</param>
    /// <param name="cancellationToken">Cancels work before durable desired publication.</param>
    Task<SettingsAuthorityResult> ApplyChangesAdmittedAsync(
        IEnumerable<SettingValueChange> changes, Guid transactionId,
        MutationAdmissionLease admissionLease, CancellationToken cancellationToken);

    /// <summary>Reverts selected desired values to verified evidence or explicit safe fallbacks and applies affected work.</summary>
    /// <param name="keys">Canonical keys to revert as one change set.</param>
    /// <param name="transactionId">Stable identity of the revert command.</param>
    /// <param name="cancellationToken">Cancels work before publication.</param>
    Task<SettingsAuthorityResult> RevertAsync(IEnumerable<SettingKey> keys, Guid transactionId, CancellationToken cancellationToken);

    /// <summary>Explicitly retries a failed attempt under a fresh identity and verifies its effect.</summary>
    /// <remarks>Acquires exclusive admission before resolving the failed batch so runtime producers cannot form a wait cycle.</remarks>
    /// <param name="batchId">Exact failed batch.</param>
    /// <param name="expectedAttemptId">Identity of the failed attempt being replaced.</param>
    /// <param name="newAttemptId">Fresh nonempty retry identity.</param>
    /// <param name="cancellationToken">Cancels work before durable retry publication.</param>
    Task<SettingsAuthorityResult> RetryAsync(
        Guid batchId, Guid expectedAttemptId, Guid newAttemptId, CancellationToken cancellationToken);

    /// <summary>Reobserves previous-process evidence and reconciles startup work under exclusive admission.</summary>
    /// <param name="startupId">Fresh nonempty startup identity.</param>
    /// <param name="admissionLease">Exclusive lease retained through startup application.</param>
    /// <param name="cancellationToken">Cancels work before startup reconciliation is durable.</param>
    Task<SettingsAuthorityResult> ReconcileStartupAdmittedAsync(
        Guid startupId, MutationAdmissionLease admissionLease, CancellationToken cancellationToken);
}
