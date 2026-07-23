using ClashSharp.Infrastructure.Triggers;

return await TriggerProbeProgram.RunAsync(args);

internal static class TriggerProbeProgram
{
    private const int CrashExitCode = 86;

    public static async Task<int> RunAsync(IReadOnlyList<string> args)
    {
        if (args.Count != 3
            || !Enum.TryParse(args[2], ignoreCase: false, out TriggerPersistenceFaultPoint faultPoint)
            || !Enum.IsDefined(faultPoint))
        {
            return 64;
        }

        string rootPath = Path.GetFullPath(args[1]);
        SqliteTriggerRepository repository = new(
            Path.Combine(rootPath, "Triggers.db"),
            new TerminatingFaultInjector(faultPoint));
        return args[0] switch
        {
            "migrate" => await MigrateAsync(repository, rootPath),
            "backup" => await BackupAsync(repository),
            _ => 64,
        };
    }

    private static async Task<int> MigrateAsync(
        SqliteTriggerRepository repository,
        string rootPath)
    {
        TriggerMigrationCoordinator coordinator = new(
            repository,
            Path.Combine(rootPath, "Triggers.json"));
        TriggerMigrationResult result = await coordinator.MigrateAsync(CancellationToken.None);
        return result.Status is TriggerMigrationStatus.Migrated or TriggerMigrationStatus.Finalized
            ? 0
            : 1;
    }

    private static async Task<int> BackupAsync(SqliteTriggerRepository repository)
    {
        if (!(await repository.OpenAsync(CancellationToken.None)).IsSucceeded)
        {
            return 1;
        }

        return (await repository.CreateBackupAsync(CancellationToken.None)).IsSucceeded ? 0 : 1;
    }

    private sealed class TerminatingFaultInjector(TriggerPersistenceFaultPoint faultPoint)
        : ITriggerPersistenceFaultInjector
    {
        public Task InjectAsync(
            TriggerPersistenceFaultPoint observedFaultPoint,
            CancellationToken cancellationToken)
        {
            if (observedFaultPoint == faultPoint)
            {
                Environment.Exit(CrashExitCode);
            }

            return Task.CompletedTask;
        }
    }
}
