namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Owns data removal across runtime shutdown, host disposal and process resource release.</summary>
public interface IApplicationLifetimeMaintenance
{
    /// <summary>Prepares verified runtime shutdown without deleting any persisted user data.</summary>
    Task PrepareShutdownAsync(CancellationToken cancellationToken);

    /// <summary>Clears host-owned preferences and credentials after shutdown commits, before disposal.</summary>
    Task ClearHostDataAsync(CancellationToken cancellationToken);

    /// <summary>Deletes local files after the host and all process-owned writers have been released.</summary>
    Task ClearLocalFilesAsync(CancellationToken cancellationToken);
}
