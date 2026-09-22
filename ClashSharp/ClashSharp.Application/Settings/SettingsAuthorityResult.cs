using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Classifies a settings command independently of whether desired values are durable.</summary>
public enum SettingsAuthorityStatus
{
    /// <summary>The requested command completed and its resulting envelope was verified.</summary>
    Succeeded,
    /// <summary>The requested edit already matched the verified envelope.</summary>
    NoChange,
    /// <summary>The command was stale, invalid, or incompatible with the current application state.</summary>
    Rejected,
    /// <summary>Storage did not verify the requested commit.</summary>
    PersistenceFailed,
    /// <summary>The participant did not verify the target; failed work remains durable.</summary>
    ApplicationFailed,
    /// <summary>The batch requires startup ownership and was left unchanged.</summary>
    DeferredToRestart,
}

/// <summary>Contains the verified outcome of a command without equating persistence with runtime application.</summary>
public sealed class SettingsAuthorityResult
{
    internal SettingsAuthorityResult(
        SettingsAuthorityStatus status, SettingsEnvelope? envelope, string? code = null,
        SettingsPersistenceStatus? persistenceStatus = null)
    {
        Status = status;
        Envelope = envelope;
        Code = code;
        PersistenceStatus = persistenceStatus;
    }

    /// <summary>Gets the command outcome.</summary>
    public SettingsAuthorityStatus Status { get; }

    /// <summary>Gets the last verified envelope, when one was observed by this command.</summary>
    public SettingsEnvelope? Envelope { get; }

    /// <summary>Gets a stable value-free diagnostic.</summary>
    public string? Code { get; }

    /// <summary>Gets the storage classification when persistence failed.</summary>
    public SettingsPersistenceStatus? PersistenceStatus { get; }

    /// <summary>Gets whether the requested command completed successfully.</summary>
    public bool IsSucceeded => Status is SettingsAuthorityStatus.Succeeded or SettingsAuthorityStatus.NoChange;
}
