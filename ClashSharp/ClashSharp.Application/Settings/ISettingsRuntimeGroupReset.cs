using ClashSharp.ApplicationModel.Data;
using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Resets runtime settings through their authoritative desired and observed state.</summary>
public interface ISettingsRuntimeGroupReset
{
    /// <summary>Resets exactly the startup, proxy, or transparent-proxy group and observes its effects.</summary>
    Task<SettingsRuntimeGroupResetResult> ResetRuntimeGroupAsync(
        SettingsResetScope scope, bool canUseTransparentProxy, CancellationToken cancellationToken);

    /// <summary>Explicitly retries the failed applications shown by one earlier result, rejecting stale identities.</summary>
    Task<SettingsRuntimeGroupResetResult> RetryRuntimeGroupAsync(
        SettingsRuntimeGroupResetResult previous, CancellationToken cancellationToken);
}

/// <summary>Retains the data identity and verified outcome required for an explicit page retry.</summary>
/// <param name="Generation">The exact data directory that owned the command.</param>
/// <param name="Scope">The selected runtime settings group.</param>
/// <param name="Outcome">The verified desired values and application evidence.</param>
/// <param name="NotificationFailure">A post-command observer failure; it never changes the durable outcome.</param>
public sealed record SettingsRuntimeGroupResetResult(
    DataGenerationDescriptor Generation, SettingsResetScope Scope, SettingsAuthorityResult Outcome, Exception? NotificationFailure = null)
{
    /// <summary>Gets whether pending application or an incomplete UI publication requires a new process.</summary>
    public bool RequiresRestart => NotificationFailure is not null || Outcome.Status == SettingsAuthorityStatus.DeferredToRestart;

    /// <summary>Gets whether this result contains failed work in the selected group.</summary>
    public bool CanRetry => Outcome.Status == SettingsAuthorityStatus.ApplicationFailed && FailedBatches.Count > 0;

    internal IReadOnlyList<SettingsApplicationBatch> FailedBatches
    {
        get
        {
            HashSet<SettingKey> keys = SettingsRegistry.Default.GetResetDefinitions(Scope).Select(definition => definition.Key).ToHashSet();
            return Outcome.Envelope?.PendingApplications.Where(batch => batch.State == SettingsApplicationBatchState.Failed
                && batch.Entries.Any(entry => keys.Contains(entry.Key))).ToArray() ?? [];
        }
    }
}

/// <summary>Reports a reset whose requested runtime state has not been verified.</summary>
public sealed class SettingsRuntimeGroupResetException : InvalidOperationException
{
    /// <summary>Preserves a value-free diagnostic and command status for page error reporting.</summary>
    public SettingsRuntimeGroupResetException(SettingsAuthorityResult outcome)
        : base($"Runtime settings reset failed: {outcome.Status} ({outcome.Code}).")
    {
        Status = outcome.Status;
        Code = outcome.Code;
    }

    /// <summary>Gets the authoritative command status.</summary>
    public SettingsAuthorityStatus Status { get; }

    /// <summary>Gets the value-free diagnostic code.</summary>
    public string? Code { get; }
}
