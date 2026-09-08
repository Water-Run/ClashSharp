namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Supplies one already-admitted package import and its runtime participants.</summary>
public interface ISettingsImportOperation : ISettingsRuntimeParticipants
{
    /// <summary>Imports a validated package and transfers its retained receipt to the coordinator.</summary>
    Task<IRetainedSettingsTransactionReceipt> BeginImportAsync(string packagePath, CancellationToken cancellationToken);

    /// <summary>Invalidates profile state after either importing or restoring the retained baseline.</summary>
    void InvalidateProfiles();
}
