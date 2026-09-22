namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Reads legacy preferences once during migration without exposing any writer.</summary>
public interface ILegacySettingsSource
{
    /// <summary>Reads one atomic allowlisted legacy preference snapshot.</summary>
    /// <param name="cancellationToken">Cancels observation before a migration write starts.</param>
    Task<LegacySettingsSnapshot> ReadSnapshotAsync(CancellationToken cancellationToken);
}
