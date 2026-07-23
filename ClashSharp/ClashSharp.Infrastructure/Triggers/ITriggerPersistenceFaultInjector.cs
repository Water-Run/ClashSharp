namespace ClashSharp.Infrastructure.Triggers;

/// <summary>Identifies deterministic trigger persistence cut points used by recovery tests.</summary>
public enum TriggerPersistenceFaultPoint
{
    /// <summary>Immediately before an execution transaction commits.</summary>
    BeforeExecutionCommit = 0,

    /// <summary>Immediately after an execution transaction commits.</summary>
    AfterExecutionCommit = 1,

    /// <summary>Before a SQLite backup destination is created.</summary>
    BeforeBackup = 2,

    /// <summary>After SQLite Backup completes but before validation.</summary>
    AfterBackup = 3,

    /// <summary>After backup validation and durable flush.</summary>
    AfterBackupValidation = 4,

    /// <summary>Immediately before the validated backup is atomically promoted.</summary>
    BeforeBackupPromotion = 5,

    /// <summary>Immediately after backup promotion.</summary>
    AfterBackupPromotion = 6,
}

/// <summary>Injects deterministic failures at durable trigger persistence boundaries.</summary>
public interface ITriggerPersistenceFaultInjector
{
    /// <summary>Observes one cut point and may fail or pause the operation.</summary>
    Task InjectAsync(
        TriggerPersistenceFaultPoint faultPoint,
        CancellationToken cancellationToken);
}

internal sealed class NullTriggerPersistenceFaultInjector : ITriggerPersistenceFaultInjector
{
    public static NullTriggerPersistenceFaultInjector Instance { get; } = new();

    public Task InjectAsync(
        TriggerPersistenceFaultPoint faultPoint,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
