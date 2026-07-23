using ClashSharp.ApplicationModel.Triggers;

namespace ClashSharp.Infrastructure.Triggers;

public sealed partial class SqliteTriggerRepository
{
    private async Task<TriggerPersistenceResult> CreateBackupCoreAsync(
        CancellationToken cancellationToken)
    {
        TriggerBackupManager backupManager = new(
            _databasePath,
            _backupPath,
            _busyTimeoutMilliseconds,
            _faultInjector);
        await backupManager.CreateAsync(cancellationToken).ConfigureAwait(false);
        return TriggerPersistenceResult.Succeeded();
    }
}
