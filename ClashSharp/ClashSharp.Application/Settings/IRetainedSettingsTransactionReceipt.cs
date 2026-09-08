namespace ClashSharp.ApplicationModel.Settings;

/// <summary>Owns a retained settings/data transaction through one durable terminal decision.</summary>
public interface IRetainedSettingsTransactionReceipt : IAsyncDisposable
{
    /// <summary>Finalizes the commit decision and completes retained-backup cleanup.</summary>
    Task CommitAsync(CancellationToken cancellationToken);

    /// <summary>Restores the complete retained baseline and completes rollback cleanup.</summary>
    Task RollbackAsync(CancellationToken cancellationToken);
}
