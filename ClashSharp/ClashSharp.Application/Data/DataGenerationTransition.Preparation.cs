using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.ApplicationModel.Data;

public sealed partial class DataGenerationTransition
{
    /// <summary>Owns complete candidate preparation and atomically claims the resulting scope before shutdown can dispose its dependencies.</summary>
    /// <param name="prepare">Factory that returns one unclaimed scope and owns cleanup until it returns.</param>
    /// <param name="cancellationToken">Cancels preparation or rejects staging before ownership transfer.</param>
    /// <returns>The detached descriptor of the candidate now owned by this transition.</returns>
    public async Task<DataGenerationDescriptor> PrepareAndStageAsync(
        Func<CancellationToken, Task<DataGenerationScope>> prepare, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(prepare);
        cancellationToken.ThrowIfCancellationRequested();
        DataGenerationManager owner = GetOwner();
        object ownership = owner.BeginStoreOperation(this);
        DataGenerationScope? unclaimed = null;
        try
        {
            if (StagedScope is not null) { throw new InvalidOperationException("The transition already owns a candidate."); }
            unclaimed = await prepare(cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Candidate preparation returned no owned scope.");
            cancellationToken.ThrowIfCancellationRequested();
            owner.Stage(this, unclaimed, ownership);
            DataGenerationDescriptor descriptor = unclaimed.Descriptor;
            unclaimed = null;
            return descriptor;
        }
        catch (Exception failure) when (!ExceptionGraphClassifier.IsProcessFatal(failure))
        {
            if (unclaimed is not null)
            {
                try { await unclaimed.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) when (!ExceptionGraphClassifier.IsProcessFatal(cleanup))
                {
                    throw new AggregateException("Candidate preparation and releasing its unclaimed resources both failed.", failure, cleanup);
                }
            }
            throw;
        }
        finally { owner.EndStoreOperation(this, ownership); }
    }
}
