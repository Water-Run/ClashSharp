namespace ClashSharp.Infrastructure.Settings;

/// <summary>Identifies deterministic settings persistence commit boundaries.</summary>
public enum SettingsPersistenceFaultPoint
{
    /// <summary>Immediately before a verified backup candidate is promoted.</summary>
    BeforeBackupPromotion = 0,

    /// <summary>Immediately after backup promotion has crossed its durable cut.</summary>
    AfterBackupPromotion = 1,

    /// <summary>Immediately before a verified envelope candidate is promoted.</summary>
    BeforeEnvelopePromotion = 2,

    /// <summary>Immediately after envelope promotion has crossed its durable cut.</summary>
    AfterEnvelopePromotion = 3,
}

/// <summary>Injects deterministic failure, pause, or process termination at persistence cuts.</summary>
public interface ISettingsPersistenceFaultInjector
{
    /// <summary>Observes one persistence cut and may fail or terminate.</summary>
    Task InjectAsync(
        SettingsPersistenceFaultPoint faultPoint,
        CancellationToken cancellationToken);
}

internal sealed class NullSettingsPersistenceFaultInjector
    : ISettingsPersistenceFaultInjector
{
    public Task InjectAsync(
        SettingsPersistenceFaultPoint faultPoint,
        CancellationToken cancellationToken) =>
        Task.CompletedTask;
}
