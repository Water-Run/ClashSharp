using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

internal sealed class DataGenerationTestDirectory : IAsyncDisposable
{
    public DataGenerationTestDirectory()
    {
        RootPath = Path.Combine(
            Path.GetTempPath(),
            $"ClashSharp-DataGeneration-{Guid.NewGuid():N}");
        Policy = new DataGenerationPathPolicy(RootPath);
        Store = new FileDataGenerationStore(RootPath);
    }

    public string RootPath { get; }

    public DataGenerationPathPolicy Policy { get; }

    public FileDataGenerationStore Store { get; }

    public DataGenerationDescriptor CreateGeneration(long generationNumber, Guid? generationId = null)
    {
        Guid resolvedId = generationId ?? Guid.NewGuid();
        return Policy.CreateGeneration(resolvedId, generationNumber);
    }

    public async Task<DataGenerationManifestSnapshot> PromoteFirstAsync()
    {
        DataGenerationDescriptor descriptor = CreateGeneration(1);
        return await Store.PromoteAsync(descriptor, null, CancellationToken.None);
    }

    public ValueTask DisposeAsync()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }

        return ValueTask.CompletedTask;
    }
}
