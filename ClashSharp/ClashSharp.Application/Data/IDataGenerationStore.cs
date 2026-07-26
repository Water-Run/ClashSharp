namespace ClashSharp.ApplicationModel.Data;

/// <summary>Loads and atomically replaces the durable current-generation manifest.</summary>
public interface IDataGenerationStore
{
    /// <summary>Loads and validates the current manifest, or returns null when none exists.</summary>
    /// <param name="cancellationToken">Cancels read work.</param>
    /// <returns>The verified durable manifest, or null.</returns>
    Task<DataGenerationManifestSnapshot?> LoadCurrentAsync(CancellationToken cancellationToken);

    /// <summary>Promotes the next generation using optimistic manifest concurrency.</summary>
    /// <param name="descriptor">Fully staged next generation.</param>
    /// <param name="expectedCurrentHash">Expected current hash, or null for the first generation.</param>
    /// <param name="cancellationToken">Cancels work before atomic promotion.</param>
    /// <returns>The verified manifest that became authoritative.</returns>
    Task<DataGenerationManifestSnapshot> PromoteAsync(
        DataGenerationDescriptor descriptor,
        string? expectedCurrentHash,
        CancellationToken cancellationToken);

    /// <summary>Restores the exact baseline descriptor while retaining allocated-number high water.</summary>
    /// <param name="baseline">Verified manifest that preceded the uncommitted promotion.</param>
    /// <param name="expectedCurrentHash">Expected hash of the promoted candidate.</param>
    /// <param name="cancellationToken">Cancels work before atomic restoration.</param>
    /// <returns>The verified restoration manifest that became authoritative.</returns>
    Task<DataGenerationManifestSnapshot> RestoreAsync(
        DataGenerationManifestSnapshot baseline,
        string expectedCurrentHash,
        CancellationToken cancellationToken);
}
