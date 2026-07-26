using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.ApplicationModel.Data;
using Microsoft.Win32.SafeHandles;

namespace ClashSharp.Infrastructure.Data;

internal static class DataGenerationIdentityMarker
{
    public const string FileName = ".generation-identity.json";

    private const int SchemaVersion = 1;
    private const int MaximumMarkerBytes = 4096;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public static void CreateAndFlush(DataGenerationDescriptor descriptor)
    {
        MarkerDocument document = CreateDocument(descriptor);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
        string path = GetPath(descriptor);
        try
        {
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.WriteThrough);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        catch (IOException exception) when (File.Exists(path))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.DuplicateGeneration,
                "The generation identity marker already exists.",
                exception);
        }

        Validate(descriptor);
    }

    public static void Validate(DataGenerationDescriptor descriptor)
    {
        string path = GetPath(descriptor);
        if (!File.Exists(path))
        {
            throw new DataGenerationStoreException(
                DataGenerationStoreError.InvalidDescriptor,
                "The immutable generation identity marker is missing.");
        }

        try
        {
            DataGenerationPathPolicy.ValidateNoReparsePoints(
                path,
                File.GetAttributes);
            using SafeFileHandle handle = ReparseSafeFile.OpenRead(
                path,
                FileShare.Read | FileShare.Delete);
            DataGenerationPathPolicy.ValidateNoReparsePoints(
                path,
                File.GetAttributes);
            long markerLength = RandomAccess.GetLength(handle);
            if (markerLength is <= 0 or > MaximumMarkerBytes)
            {
                throw CreateInvalidMarkerException(
                    "The immutable generation identity marker has an invalid size.");
            }

            byte[] bytes = new byte[checked((int)markerLength)];
            int bytesRead = 0;
            while (bytesRead < bytes.Length)
            {
                int read = RandomAccess.Read(
                    handle,
                    bytes.AsSpan(bytesRead),
                    bytesRead);
                if (read == 0)
                {
                    throw CreateInvalidMarkerException(
                        "The immutable generation identity marker ended unexpectedly.");
                }

                bytesRead += read;
            }

            MarkerDocument document = JsonSerializer.Deserialize<MarkerDocument>(
                bytes,
                SerializerOptions)
                ?? throw CreateInvalidMarkerException(
                    "The immutable generation identity marker is empty.");
            byte[] canonicalBytes =
                JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
            if (!bytes.AsSpan().SequenceEqual(canonicalBytes)
                || document.SchemaVersion != SchemaVersion
                || document.GenerationId != descriptor.GenerationId
                || document.GenerationNumber != descriptor.GenerationNumber)
            {
                throw CreateInvalidMarkerException(
                    "The immutable generation identity marker does not match its descriptor.");
            }
        }
        catch (DataGenerationStoreException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or IOException)
        {
            throw CreateInvalidMarkerException(
                "The immutable generation identity marker cannot be read.",
                exception);
        }
    }

    private static MarkerDocument CreateDocument(DataGenerationDescriptor descriptor)
    {
        return new MarkerDocument(
            SchemaVersion,
            descriptor.GenerationId,
            descriptor.GenerationNumber);
    }

    private static string GetPath(DataGenerationDescriptor descriptor)
    {
        return Path.Combine(descriptor.RootPath, FileName);
    }

    private static DataGenerationStoreException CreateInvalidMarkerException(
        string message,
        Exception? innerException = null)
    {
        return new DataGenerationStoreException(
            DataGenerationStoreError.InvalidDescriptor,
            message,
            innerException);
    }

    private sealed record MarkerDocument(
        int SchemaVersion,
        Guid GenerationId,
        long GenerationNumber);
}
