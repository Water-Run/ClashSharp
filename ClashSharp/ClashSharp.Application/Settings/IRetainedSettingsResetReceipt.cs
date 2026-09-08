namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Owns a retained reset until its durable commit or rollback decision is finalized.</summary>
public interface IRetainedSettingsResetReceipt : IAsyncDisposable
{
    /// <summary>Commits the reset and completes cleanup without reversing a prior commit decision.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Restores the complete retained settings snapshot and completes rollback cleanup.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}
