namespace ClashSharp.Infrastructure.Data;

/// <summary>Identifies deterministic durable current-manifest cut points.</summary>
public enum DataGenerationFaultPoint
{
    /// <summary>After the candidate file is flushed but before promotion begins.</summary>
    AfterTemporaryFlush,

    /// <summary>Immediately before atomic current-manifest promotion.</summary>
    BeforeManifestPromotion,

    /// <summary>Immediately after atomic current-manifest promotion.</summary>
    AfterManifestPromotion,
}

/// <summary>Injects deterministic failures or pauses at generation-manifest boundaries.</summary>
public interface IDataGenerationFaultInjector
{
    /// <summary>Observes one persistence cut point and may pause or fail.</summary>
    /// <param name="faultPoint">Current durable cut point.</param>
    /// <param name="cancellationToken">Cancellation before the commit boundary.</param>
    Task InjectAsync(
        DataGenerationFaultPoint faultPoint,
        CancellationToken cancellationToken);
}

internal sealed class NullDataGenerationFaultInjector : IDataGenerationFaultInjector
{
    public Task InjectAsync(
        DataGenerationFaultPoint faultPoint,
        CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}
