using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Exposes persisted application failures without replaying them during ordinary page reads.</summary>
public interface ISettingsApplicationRecovery
{
    /// <summary>Captures the current immutable desired/application state and data identity.</summary>
    SettingsAuthoritySnapshot CaptureSnapshot();

    /// <summary>Explicitly retries one exact failed application shown to the user.</summary>
    Task<SettingsApplicationRecoveryResult> RetryApplicationAsync(SettingsAuthoritySnapshot expected,
        Guid batchId, CancellationToken cancellationToken);
}

/// <summary>Preserves the verified retry outcome independently of observer delivery.</summary>
/// <param name="Generation">Data directory that owned this attempt.</param>
/// <param name="Outcome">Verified persistence and application result.</param>
/// <param name="NotificationFailure">An observer failed after the operation completed.</param>
public sealed record SettingsApplicationRecoveryResult(DataGenerationDescriptor Generation,
    SettingsAuthorityResult Outcome, Exception? NotificationFailure = null);
