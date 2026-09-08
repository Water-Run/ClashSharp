using ClashSharp.Settings;

namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Supplies already-admitted storage, runtime participants, and applied-state observation.</summary>
/// <remarks>The caller owns admission and serialization until the coordinator and receipt finish.</remarks>
public interface ISettingsResetOperation : ISettingsRuntimeParticipants
{
    /// <summary>Resets exactly one supported scope and transfers its retained receipt to the coordinator.</summary>
    IRetainedSettingsResetReceipt BeginReset(SettingsResetScope scope, bool transparentProxyEnabled);

    /// <summary>Restores participant-facing values for the legacy full-reset adapter.</summary>
    void RestoreDurableSnapshot(SettingsRuntimeSnapshot snapshot);

    /// <summary>Publishes a verified snapshot before retained-decision cleanup is attempted.</summary>
    void ReportApplied(SettingsRuntimeSnapshot snapshot, SettingsResetScope scope, bool operationFailed);
}
