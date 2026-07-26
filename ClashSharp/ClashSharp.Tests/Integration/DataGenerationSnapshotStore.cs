using ClashSharp.ApplicationModel.Data;

namespace ClashSharp.Tests.Integration;

internal sealed class DataGenerationSnapshotStore(
    DataGenerationManifestSnapshot current,
    DataGenerationManifestSnapshot? restorationResult = null) : IDataGenerationStore
{
    public Task<DataGenerationManifestSnapshot?> LoadCurrentAsync(
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<DataGenerationManifestSnapshot?>(current);
    }

    public Task<DataGenerationManifestSnapshot> PromoteAsync(
        DataGenerationDescriptor descriptor,
        string? expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        throw new NotSupportedException();
    }

    public Task<DataGenerationManifestSnapshot> RestoreAsync(
        DataGenerationManifestSnapshot baseline,
        string expectedCurrentHash,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            restorationResult
            ?? throw new NotSupportedException());
    }
}
