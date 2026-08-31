using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using ClashSharp.Installer.Contracts;
using ClashSharp.Installer.Payloads;
using ClashSharp.Installer.Windows.Files;

namespace ClashSharp.Installer.Windows.Machines;

/// <summary>
/// Reads only the seven manifest-bound machine files from the already locked primary MSIX.
/// The caller owns destination creation, ACLs, durable flushes, and the final directory swap.
/// </summary>
internal sealed class WindowsMachinePayloadArchive : IAsyncDisposable
{
    private readonly WindowsMachineDeploymentPlan _plan;
    private readonly FileStream _packageStream;
    private readonly ZipArchive _archive;
    private readonly IReadOnlyDictionary<string, ZipArchiveEntry> _entries;
    private bool _disposed;

    private WindowsMachinePayloadArchive(
        WindowsMachineDeploymentPlan plan,
        FileStream packageStream,
        ZipArchive archive,
        IReadOnlyDictionary<string, ZipArchiveEntry> entries)
    {
        _plan = plan;
        _packageStream = packageStream;
        _archive = archive;
        _entries = entries;
    }

    internal static WindowsMachinePayloadArchive Open(
        WindowsMachineDeploymentPlan plan,
        WindowsInstallerReleaseLease release,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(release);
        plan.Validate();
        release.RequireRequest(plan.Request);
        cancellationToken.ThrowIfCancellationRequested();
        RequireExactManifest(plan.Manifest, release.Manifest);

        InstallerPayloadFileEntry packageEntry = release.Manifest.Files.Single(static file =>
            file.Role == InstallerPayloadFileRole.PrimaryPackage);
        FileStream packageStream = release.RequireFile(packageEntry).OpenVerifiedReadStream();
        ZipArchive? archive = null;
        try
        {
            archive = new ZipArchive(packageStream, ZipArchiveMode.Read, leaveOpen: true);
            IReadOnlyDictionary<string, ZipArchiveEntry> entries = RequireExactEntries(
                archive,
                plan,
                cancellationToken);
            return new WindowsMachinePayloadArchive(
                plan,
                packageStream,
                archive,
                entries);
        }
        catch
        {
            archive?.Dispose();
            packageStream.Dispose();
            throw;
        }
    }

    internal async Task CopyToAsync(
        WindowsMachinePayloadTarget target,
        Stream destination,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();
        RequirePlanTarget(target);
        if (!destination.CanWrite
            || !destination.CanSeek
            || destination.Position != 0
            || destination.Length != 0)
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_destination_invalid");
        }

        ZipArchiveEntry entry = _entries[target.Source.Path];
        byte[] buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using Stream source = entry.Open();
            long copied = 0;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(
                    buffer.Length,
                    checked(target.Source.Length - copied + 1));
                int read = await source.ReadAsync(
                        buffer.AsMemory(0, requested),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                copied = checked(copied + read);
                if (copied > target.Source.Length)
                {
                    throw new InstallerProtocolException(
                        "installer.machine.payload_source_changed");
                }

                hash.AppendData(buffer.AsSpan(0, read));
                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            byte[] digest = hash.GetHashAndReset();
            try
            {
                if (copied != target.Source.Length
                    || destination.Position != target.Source.Length
                    || !string.Equals(
                        Convert.ToHexStringLower(digest),
                        target.Source.Sha256,
                        StringComparison.Ordinal))
                {
                    throw new InstallerProtocolException(
                        "installer.machine.payload_source_changed");
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(digest);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(buffer);
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return ValueTask.CompletedTask;
        }

        _archive.Dispose();
        _packageStream.Dispose();
        _disposed = true;
        return ValueTask.CompletedTask;
    }

    private static IReadOnlyDictionary<string, ZipArchiveEntry> RequireExactEntries(
        ZipArchive archive,
        WindowsMachineDeploymentPlan plan,
        CancellationToken cancellationToken)
    {
        if (archive.Entries.Count > InstallerPayloadBudgets.MaximumPackageArchiveEntries)
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_archive_invalid");
        }

        Dictionary<string, InstallerMachinePayloadFileEntry> expected = plan.PayloadTargets
            .Select(static target => target.Source)
            .ToDictionary(static entry => entry.Path, StringComparer.Ordinal);
        var observed = new Dictionary<string, ZipArchiveEntry>(StringComparer.Ordinal);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string normalizedPath = entry.FullName.ToLowerInvariant();
            if (!IsMachinePath(normalizedPath))
            {
                continue;
            }

            if (!expected.TryGetValue(
                    normalizedPath,
                    out InstallerMachinePayloadFileEntry? expectedEntry)
                || entry.Length != expectedEntry.Length
                || !observed.TryAdd(normalizedPath, entry))
            {
                throw new InstallerProtocolException(
                    "installer.machine.payload_archive_invalid");
            }
        }

        if (observed.Count != expected.Count)
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_archive_invalid");
        }

        return observed;
    }

    private static bool IsMachinePath(string path) =>
        string.Equals(path, "binaries/mihomo.exe", StringComparison.Ordinal)
        || path.StartsWith("binaries/service/", StringComparison.Ordinal)
        || path.StartsWith("binaries/geodata/", StringComparison.Ordinal);

    private static void RequireExactManifest(
        InstallerReleaseManifest expected,
        InstallerReleaseManifest actual)
    {
        byte[] expectedBytes = InstallerReleaseManifestCodec.Serialize(expected);
        byte[] actualBytes = InstallerReleaseManifestCodec.Serialize(actual);
        try
        {
            if (!CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes))
            {
                throw new InstallerProtocolException(
                    "installer.release.identity_mismatch");
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(expectedBytes);
            CryptographicOperations.ZeroMemory(actualBytes);
        }
    }

    private void RequirePlanTarget(WindowsMachinePayloadTarget target)
    {
        int matches = _plan.PayloadTargets.Count(candidate => candidate == target);
        if (matches != 1)
        {
            throw new InstallerProtocolException(
                "installer.machine.payload_target_invalid");
        }
    }
}
