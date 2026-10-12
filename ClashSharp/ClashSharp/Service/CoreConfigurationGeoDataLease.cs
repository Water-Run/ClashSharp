using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

/// <summary>Holds verified bundled GeoData until validation either releases it or retains it for runtime use.</summary>
/// <remarks>
/// Existing caches are read-locked, never replaced; mihomo validates their contents. New copies
/// match the bundled manifest. Temporary copies are removed unless successful runtime validation retains
/// them as writable caches for the subsequent core. The directory gate covers preparation and release.
/// </remarks>
internal sealed class CoreConfigurationGeoDataLease : IDisposable
{
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private static readonly string[] AssetNames = ["Country.mmdb", "GeoIP.dat", "GeoSite.dat", "ASN.mmdb"];
    private static readonly ProfileOperationGate DirectoryGate = new();
    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    private readonly List<FileStream> _files = [];
    private readonly List<string> _createdFiles = [];
    private IDisposable? _directoryLease;

    private CoreConfigurationGeoDataLease(IDisposable directoryLease) => _directoryLease = directoryLease;

    /// <summary>Prepares the fixed GeoData set without changing configuration or retaining duplicate assets.</summary>
    internal static async Task<CoreConfigurationGeoDataLease> AcquireAsync(
        string bundledDirectory,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bundledDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        cancellationToken.ThrowIfCancellationRequested();
        string sourceRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundledDirectory));
        string targetRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
        EnsureOrdinaryDirectory(sourceRoot);
        EnsureOrdinaryDirectory(targetRoot);
        if (string.Equals(sourceRoot, targetRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("The profile validation directory must differ from bundled assets.");
        }

        IDisposable directoryLease = await DirectoryGate.EnterAsync(targetRoot.ToUpperInvariant(), cancellationToken).ConfigureAwait(false);
        CoreConfigurationGeoDataLease lease = new(directoryLease);
        try
        {
            await using FileStream manifestStream = OpenOrdinaryFile(Path.Combine(sourceRoot, "manifest.json"), 64 * 1024);
            Manifest manifest;
            try
            {
                manifest = await JsonSerializer.DeserializeAsync<Manifest>(manifestStream, ManifestOptions, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidDataException("The bundled GeoData manifest is empty.");
            }
            catch (JsonException exception)
            {
                throw new InvalidDataException("The bundled GeoData manifest is invalid.", exception);
            }
            if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Length != AssetNames.Length)
            {
                throw new InvalidDataException("The bundled GeoData manifest is incomplete.");
            }

            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (Entry entry in manifest.Files)
            {
                if (entry is null || !AssetNames.Contains(entry.Name, StringComparer.Ordinal) || !names.Add(entry.Name)
                    || entry.Length is < 1 or > MaximumAssetBytes || entry.Sha256 is null || entry.Sha256.Length != 64
                    || !entry.Sha256.All(char.IsAsciiHexDigit))
                {
                    throw new InvalidDataException("The bundled GeoData manifest contains an invalid asset entry.");
                }
            }

            foreach (Entry entry in manifest.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await using FileStream source = OpenOrdinaryFile(Path.Combine(sourceRoot, entry.Name), MaximumAssetBytes);
                await VerifyAsync(source, entry, cancellationToken).ConfigureAwait(false);
                source.Position = 0;
                string targetPath = Path.Combine(targetRoot, entry.Name);
                if (File.Exists(targetPath))
                {
                    FileStream existing = OpenOrdinaryFile(targetPath, MaximumAssetBytes);
                    lease._files.Add(existing);
                    // A native auto-update may have produced a newer valid database than the bundle.
                    // Preserve it and let mihomo validate it instead of treating a different hash as damage.
                    continue;
                }

                // CreateNew cannot truncate a pre-existing profile asset; retained handles deny replacement.
                await using (FileStream target = new(targetPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                    128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    lease._createdFiles.Add(targetPath);
                    await source.CopyToAsync(target, cancellationToken).ConfigureAwait(false);
                    await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                // Native readers may deny sharing with a writer; retain only read access during validation.
                FileStream retained = OpenOrdinaryFile(targetPath, MaximumAssetBytes);
                lease._files.Add(retained);
                await VerifyAsync(retained, entry, cancellationToken).ConfigureAwait(false);
            }
            return lease;
        }
        catch (Exception preparationFailure)
        {
            try { lease.Dispose(); }
            catch (Exception cleanupFailure)
            {
                throw new AggregateException("GeoData preparation and cleanup both failed.", preparationFailure, cleanupFailure);
            }
            throw;
        }
    }

    /// <summary>Retains verified copies for a subsequent runtime; disposal still releases every handle and the gate.</summary>
    /// <remarks>Only a successfully completed runtime validation may choose this; ordinary profile previews stay temporary.</remarks>
    internal void RetainForRuntime()
    {
        ObjectDisposedException.ThrowIf(_directoryLease is null, this);
        _createdFiles.Clear();
    }

    /// <summary>Releases readers, removes uncommitted temporary copies, and admits another validator.</summary>
    public void Dispose()
    {
        IDisposable? directoryLease = Interlocked.Exchange(ref _directoryLease, null);
        if (directoryLease is null) { return; }
        List<Exception> failures = [];
        try
        {
            foreach (FileStream file in _files)
            {
                try { file.Dispose(); }
                catch (IOException exception) { failures.Add(exception); }
            }
            foreach (string path in _createdFiles)
            {
                try
                {
                    EnsureOrdinaryFile(path);
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failures.Add(exception);
                }
            }
        }
        finally { directoryLease.Dispose(); }
        if (failures.Count != 0)
        {
            throw new AggregateException("Temporary validation GeoData could not be removed.", failures);
        }
    }

    private static async Task VerifyAsync(FileStream stream, Entry entry, CancellationToken cancellationToken)
    {
        if (stream.Length != entry.Length
            || !string.Equals(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)), entry.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("GeoData differs from the bundled asset manifest.");
        }
    }

    private static FileStream OpenOrdinaryFile(string path, long maximumBytes)
    {
        EnsureOrdinaryFile(path);
        FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length is > 0 && stream.Length <= maximumBytes) { return stream; }
        stream.Dispose();
        throw new InvalidDataException("GeoData input exceeds its bounded file contract.");
    }

    private static void EnsureOrdinaryFile(string path)
    {
        if ((File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
        {
            throw new InvalidDataException("GeoData must be an ordinary file.");
        }
        EnsureOrdinaryDirectory(Path.GetDirectoryName(path)!);
    }

    private static void EnsureOrdinaryDirectory(string path)
    {
        for (DirectoryInfo? directory = new(path); directory is not null; directory = directory.Parent)
        {
            if (!directory.Exists || (directory.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidDataException("GeoData paths must use ordinary existing directories.");
            }
        }
    }

    private sealed record Manifest(int SchemaVersion, Entry[] Files);
    private sealed record Entry(string Name, long Length, string Sha256);
}
