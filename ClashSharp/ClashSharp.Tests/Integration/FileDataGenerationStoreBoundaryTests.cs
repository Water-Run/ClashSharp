using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Infrastructure.Data;

namespace ClashSharp.Tests.Integration;

/// <summary>Verifies restoration fault cuts and adversarial manifest boundaries.</summary>
public sealed class FileDataGenerationStoreBoundaryTests
{
    /// <summary>Verifies every restoration cut preserves the complete promoted or restored pointer.</summary>
    [Theory]
    [InlineData(DataGenerationFaultPoint.AfterTemporaryFlush, 2, 2)]
    [InlineData(DataGenerationFaultPoint.BeforeManifestPromotion, 2, 2)]
    [InlineData(DataGenerationFaultPoint.AfterManifestPromotion, 1, 3)]
    public async Task RestoreAsync_InjectedFailure_PreservesOneCompleteManifest(
        DataGenerationFaultPoint faultPoint,
        long expectedGenerationNumber,
        long expectedManifestRevision)
    {
        await using DataGenerationTestDirectory directory = new();
        DataGenerationManifestSnapshot baseline = await directory.PromoteFirstAsync();
        DataGenerationDescriptor candidate = directory.CreateGeneration(2);
        DataGenerationManifestSnapshot promoted = await directory.Store.PromoteAsync(
            candidate,
            baseline.ContentHash,
            CancellationToken.None);
        FileDataGenerationStore failingStore = new(
            directory.RootPath,
            new ThrowingFaultInjector(faultPoint));

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => failingStore.RestoreAsync(
                baseline,
                promoted.ContentHash,
                CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.Unavailable, exception.Error);
        DataGenerationManifestSnapshot loaded =
            (await directory.Store.LoadCurrentAsync(CancellationToken.None))!;
        Assert.Equal(expectedGenerationNumber, loaded.Descriptor.GenerationNumber);
        Assert.Equal(expectedManifestRevision, loaded.ManifestRevision);
        Assert.Equal(2, loaded.HighestGenerationNumber);
        Assert.True(Directory.Exists(baseline.Descriptor.RootPath));
        Assert.True(Directory.Exists(candidate.RootPath));
    }

    /// <summary>Verifies a validly hashed manifest still cannot redirect a generation root.</summary>
    [Fact]
    public async Task LoadCurrentAsync_HashedEscapingRoot_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        Guid generationId = Guid.Parse("4e10ef97-e70f-4e5f-bf25-551a84ad9c17");
        directory.CreateGeneration(1, generationId);
        directory.Policy.EnsureLayout();
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            manifestRevision = 1,
            generationId,
            generationNumber = 1,
            highestGenerationNumber = 1,
            rootRelativePath = "../escape",
        });
        string hash = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        byte[] envelope = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schemaVersion = 1,
            payload = Convert.ToBase64String(payload),
            contentHash = hash,
        });
        await File.WriteAllBytesAsync(directory.Policy.CurrentManifestPath, envelope);

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.LoadCurrentAsync(CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.Corrupt, exception.Error);
    }

    /// <summary>Verifies unknown envelope fields are rejected instead of silently normalized.</summary>
    [Fact]
    public async Task LoadCurrentAsync_UnknownEnvelopeField_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        directory.Policy.EnsureLayout();
        await File.WriteAllTextAsync(
            directory.Policy.CurrentManifestPath,
            """
            {
              "schemaVersion": 1,
              "payload": "e30=",
              "contentHash": "0000000000000000000000000000000000000000000000000000000000000000",
              "unexpected": true
            }
            """);

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.LoadCurrentAsync(CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.Corrupt, exception.Error);
    }

    /// <summary>Verifies duplicate envelope properties cannot shadow a validated value.</summary>
    [Fact]
    public async Task LoadCurrentAsync_DuplicateEnvelopeField_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        directory.Policy.EnsureLayout();
        await File.WriteAllTextAsync(
            directory.Policy.CurrentManifestPath,
            """
            {
              "schemaVersion": 1,
              "schemaVersion": 1,
              "payload": "e30=",
              "contentHash": "0000000000000000000000000000000000000000000000000000000000000000"
            }
            """);

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.LoadCurrentAsync(CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.Corrupt, exception.Error);
    }

    /// <summary>Verifies an absent staged directory is diagnosed before any manifest write.</summary>
    [Fact]
    public async Task PromoteAsync_MissingGenerationDirectory_IsRejected()
    {
        await using DataGenerationTestDirectory directory = new();
        Guid generationId = Guid.NewGuid();
        DataGenerationDescriptor descriptor = new(
            generationId,
            1,
            directory.Policy.GetGenerationRootPath(generationId));

        DataGenerationStoreException exception = await Assert.ThrowsAsync<DataGenerationStoreException>(
            () => directory.Store.PromoteAsync(descriptor, null, CancellationToken.None));

        Assert.Equal(DataGenerationStoreError.InvalidDescriptor, exception.Error);
        Assert.False(File.Exists(directory.Policy.CurrentManifestPath));
    }

    private sealed class ThrowingFaultInjector(DataGenerationFaultPoint target)
        : IDataGenerationFaultInjector
    {
        public Task InjectAsync(
            DataGenerationFaultPoint faultPoint,
            CancellationToken cancellationToken)
        {
            if (faultPoint == target)
            {
                throw new IOException("Injected restoration fault.");
            }

            return Task.CompletedTask;
        }
    }
}
