using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.ServiceProtocol;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.MihomoService;

/// <summary>
/// Rewrites provider paths and copies installer/profile assets into the protected mihomo runtime.
/// </summary>
/// <remarks>
/// HTTP downloads remain a native mihomo responsibility. The service only chooses their cache path.
/// Local provider and geodata files are copied from fixed, bounded roots before launch.
/// </remarks>
internal sealed class MihomoRuntimeAssetStager
{
    private const long MaximumProviderBytes = 16L * 1024 * 1024;
    private const long MaximumProviderAggregateBytes = 64L * 1024 * 1024;
    private const long MaximumGeoAssetBytes = 256L * 1024 * 1024;
    private const string GeoManifestFileName = "manifest.json";

    private static readonly JsonSerializerOptions ManifestOptions = new(JsonSerializerDefaults.Web)
    {
        AllowDuplicateProperties = false,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8,
    };

    private static readonly IReadOnlyDictionary<string, string> CanonicalGeoNames =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Country.mmdb"] = "Country.mmdb",
            ["GeoIP.dat"] = "GeoIP.dat",
            ["GeoSite.dat"] = "GeoSite.dat",
            ["ASN.mmdb"] = "ASN.mmdb",
        };

    private readonly string? _geoDataDirectory;
    private readonly string? _sourceAssetRoot;
    private readonly bool _protectDirectory;

    internal MihomoRuntimeAssetStager(
        string? geoDataDirectory,
        string? sourceAssetRoot,
        bool protectDirectory)
    {
        _geoDataDirectory = string.IsNullOrWhiteSpace(geoDataDirectory)
            ? null
            : Path.GetFullPath(geoDataDirectory);
        _sourceAssetRoot = string.IsNullOrWhiteSpace(sourceAssetRoot)
            ? null
            : Path.GetFullPath(sourceAssetRoot);
        _protectDirectory = protectDirectory;
    }

    internal async Task PrepareAsync(
        YamlMappingNode root,
        string sourceConfigurationPath,
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceConfigurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        string sourceRoot = _sourceAssetRoot
            ?? Path.GetDirectoryName(Path.GetFullPath(sourceConfigurationPath))
            ?? throw new InvalidDataException("The source configuration root is unavailable.");
        EnsureOrdinaryDirectory(sourceRoot, "configuration asset root");
        EnsureOrdinaryDirectory(runtimeDirectory, "protected runtime root");

        long copiedProviderBytes = 0;
        foreach ((string sectionName, MihomoServiceIpcProviderKind kind) in new[]
                 {
                     ("proxy-providers", MihomoServiceIpcProviderKind.Proxy),
                     ("rule-providers", MihomoServiceIpcProviderKind.Rule),
                 })
        {
            if (!TryGet(root, sectionName, out YamlNode? sectionNode))
            {
                continue;
            }

            YamlMappingNode providers = sectionNode as YamlMappingNode
                ?? throw new MihomoServiceConfigurationTrustException(
                    $"The '{sectionName}' section must be a mapping.");
            foreach ((YamlNode nameNode, YamlNode providerNode) in providers.Children)
            {
                string providerName = RequireScalar(nameNode, "provider name");
                YamlMappingNode provider = providerNode as YamlMappingNode
                    ?? throw new MihomoServiceConfigurationTrustException(
                        $"Provider '{providerName}' must be a mapping.");
                string type = RequireMappingScalar(provider, "type", $"provider '{providerName}'");
                if (type.Equals("inline", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string kindName = kind == MihomoServiceIpcProviderKind.Proxy ? "proxy" : "rule";
                string stableName = Convert.ToHexString(SHA256.HashData(
                        Encoding.UTF8.GetBytes(kindName + "\0" + providerName)))
                    .ToLowerInvariant();
                if (type.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    string relativeCachePath = $"providers/http/{kindName}/{stableName}.yaml";
                    string cachePath = MihomoServiceConfigurationTrustValidator.ValidateProviderPath(
                        runtimeDirectory,
                        relativeCachePath);
                    PrepareOwnedDirectory(Path.GetDirectoryName(cachePath)!);
                    SetMappingScalar(provider, "path", relativeCachePath);
                    continue;
                }

                if (!type.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    throw new MihomoServiceConfigurationTrustException(
                        $"Provider '{providerName}' has unsupported type '{type}'.");
                }

                string sourceRelativePath = RequireMappingScalar(
                    provider,
                    "path",
                    $"provider '{providerName}'");
                string[] components = MihomoServiceConfigurationTrustValidator
                    .ValidateProviderRelativePath(sourceRelativePath);
                FileInfo source;
                try
                {
                    string sourcePath = ResolveUnderRoot(sourceRoot, components);
                    source = RequireOrdinaryFile(
                        sourcePath,
                        MaximumProviderBytes,
                        $"file provider '{providerName}'");
                }
                catch (MihomoServiceConfigurationTrustException exception)
                {
                    throw new MihomoRuntimeAssetException(
                        "provider.path_invalid",
                        exception.Message);
                }

                copiedProviderBytes = checked(copiedProviderBytes + source.Length);
                if (copiedProviderBytes > MaximumProviderAggregateBytes)
                {
                    throw new MihomoRuntimeAssetException(
                        "provider.assets_too_large",
                        "Local provider files exceed the aggregate service safety limit.");
                }

                string relativeTargetPath = $"providers/file/{kindName}/{stableName}.yaml";
                string targetPath = MihomoServiceConfigurationTrustValidator.ValidateProviderPath(
                    runtimeDirectory,
                    relativeTargetPath);
                PrepareOwnedDirectory(Path.GetDirectoryName(targetPath)!);
                await CopyVerifiedFileAsync(
                        source.FullName,
                        targetPath,
                        source.Length,
                        expectedHash: null,
                        validationErrorCode: "provider.asset_changed",
                        cancellationToken: cancellationToken)
                    .ConfigureAwait(false);
                SetMappingScalar(provider, "path", relativeTargetPath);
            }
        }

        GeoRequirements requirements = DiscoverGeoRequirements(root);
        if (requirements.Any)
        {
            await StageGeoDataAsync(requirements, runtimeDirectory, cancellationToken)
                .ConfigureAwait(false);
        }

        SetMappingScalar(root, "geo-auto-update", "false");
    }

    private async Task StageGeoDataAsync(
        GeoRequirements requirements,
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        if (_geoDataDirectory is null || !Directory.Exists(_geoDataDirectory))
        {
            throw new MihomoRuntimeAssetException(
                "geo.assets_missing",
                "Required geodata assets are missing; run the Clash# installer repair action.");
        }

        try
        {
            EnsureOrdinaryDirectory(_geoDataDirectory, "installer geodata root");
        }
        catch (MihomoServiceConfigurationTrustException exception)
        {
            throw new MihomoRuntimeAssetException("geo.assets_invalid", exception.Message);
        }

        string manifestPath = Path.Combine(_geoDataDirectory, GeoManifestFileName);
        if (!File.Exists(manifestPath))
        {
            throw new MihomoRuntimeAssetException(
                "geo.assets_missing",
                "The installer geodata manifest is missing; run repair.");
        }

        FileInfo manifestFile;
        try
        {
            manifestFile = RequireOrdinaryFile(
                manifestPath,
                64 * 1024,
                "installer geodata manifest");
        }
        catch (MihomoServiceConfigurationTrustException exception)
        {
            throw new MihomoRuntimeAssetException("geo.assets_invalid", exception.Message);
        }
        GeoManifest manifest;
        try
        {
            await using FileStream stream = new(
                manifestFile.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            manifest = await JsonSerializer.DeserializeAsync<GeoManifest>(
                        stream,
                        ManifestOptions,
                        cancellationToken)
                    .ConfigureAwait(false)
                ?? throw new MihomoRuntimeAssetException(
                    "geo.assets_invalid",
                    "The installer geodata manifest is empty.");
        }
        catch (JsonException exception)
        {
            throw new MihomoRuntimeAssetException(
                "geo.assets_invalid",
                $"The installer geodata manifest is invalid ({exception.GetType().Name}).");
        }

        if (manifest.SchemaVersion != 1 || manifest.Files is null || manifest.Files.Count is < 1 or > 4)
        {
            throw new MihomoRuntimeAssetException(
                "geo.assets_invalid",
                "The installer geodata manifest has an unsupported shape.");
        }

        Dictionary<string, GeoManifestEntry> entries = new(StringComparer.OrdinalIgnoreCase);
        foreach (GeoManifestEntry entry in manifest.Files)
        {
            if (entry is null
                || !CanonicalGeoNames.TryGetValue(entry.Name, out string? canonicalName)
                || !string.Equals(entry.Name, canonicalName, StringComparison.Ordinal)
                || entry.Length is < 1 or > MaximumGeoAssetBytes
                || !MihomoServiceIpcProtocol.IsCanonicalSha256(entry.Sha256)
                || !entries.TryAdd(canonicalName, entry))
            {
                throw new MihomoRuntimeAssetException(
                    "geo.assets_invalid",
                    "The installer geodata manifest contains an invalid asset entry.");
            }
        }

        List<string> requiredNames = [];
        if (requirements.GeoSite)
        {
            requiredNames.Add("GeoSite.dat");
        }

        if (requirements.GeoIp)
        {
            requiredNames.Add(requirements.GeodataMode ? "GeoIP.dat" : "Country.mmdb");
        }

        if (requirements.Asn)
        {
            requiredNames.Add("ASN.mmdb");
        }

        foreach (string name in requiredNames.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!entries.TryGetValue(name, out GeoManifestEntry? entry))
            {
                throw new MihomoRuntimeAssetException(
                    "geo.assets_missing",
                    $"Required installer geodata asset '{name}' is not declared; run repair.");
            }

            string sourcePath = Path.Combine(_geoDataDirectory, name);
            if (!File.Exists(sourcePath))
            {
                throw new MihomoRuntimeAssetException(
                    "geo.assets_missing",
                    $"Required installer geodata asset '{name}' is missing; run repair.");
            }

            FileInfo source;
            try
            {
                source = RequireOrdinaryFile(
                    sourcePath,
                    MaximumGeoAssetBytes,
                    $"installer geodata asset '{name}'");
            }
            catch (MihomoServiceConfigurationTrustException exception)
            {
                throw new MihomoRuntimeAssetException("geo.assets_invalid", exception.Message);
            }

            if (source.Length != entry.Length)
            {
                throw new MihomoRuntimeAssetException(
                    "geo.assets_invalid",
                    $"Installer geodata asset '{name}' has an invalid length; run repair.");
            }

            await CopyVerifiedFileAsync(
                    source.FullName,
                    Path.Combine(runtimeDirectory, name),
                    entry.Length,
                    entry.Sha256,
                    "geo.assets_invalid",
                    cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task CopyVerifiedFileAsync(
        string sourcePath,
        string targetPath,
        long expectedLength,
        string? expectedHash,
        string validationErrorCode,
        CancellationToken cancellationToken)
    {
        string temporaryPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string actualHash;
            await using (FileStream source = new(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (FileStream target = new(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = new byte[64 * 1024];
                long copied = 0;
                while (true)
                {
                    int read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    copied = checked(copied + read);
                    if (copied > expectedLength)
                    {
                        throw new MihomoRuntimeAssetException(
                            validationErrorCode,
                            "A staged runtime asset changed while it was being copied.");
                    }

                    hash.AppendData(buffer, 0, read);
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                }

                await target.FlushAsync(cancellationToken).ConfigureAwait(false);
                target.Flush(flushToDisk: true);
                if (copied != expectedLength)
                {
                    throw new MihomoRuntimeAssetException(
                        validationErrorCode,
                        "A staged runtime asset changed while it was being copied.");
                }

                actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            }

            if (expectedHash is not null
                && !CryptographicOperations.FixedTimeEquals(
                    Convert.FromHexString(actualHash),
                    Convert.FromHexString(expectedHash)))
            {
                throw new MihomoRuntimeAssetException(
                    validationErrorCode,
                    "An installer geodata asset failed SHA-256 verification; run repair.");
            }

            SecureFile(temporaryPath);
            if (File.Exists(targetPath))
            {
                File.SetAttributes(targetPath, FileAttributes.Normal);
            }

            File.Move(temporaryPath, targetPath, overwrite: true);
            SecureFile(targetPath);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private void PrepareOwnedDirectory(string directoryPath)
    {
        DirectoryInfo directory = new(directoryPath);
        directory.Create();
        directory.Refresh();
        MihomoGenerationStore.ValidateOwnedDirectoryAttributes(directory.Attributes, "runtime asset directory");
        if (_protectDirectory)
        {
            directory.SetAccessControl(MihomoGenerationStore.CreateProtectedDirectorySecurity());
        }
    }

    private void SecureFile(string path)
    {
        FileInfo file = new(path);
        file.Refresh();
        MihomoGenerationStore.ValidateOwnedDirectoryAttributes(file.Attributes, "runtime asset");
        if (_protectDirectory)
        {
            file.SetAccessControl(MihomoGenerationStore.CreateProtectedFileSecurity());
        }
    }

    private static string ResolveUnderRoot(string root, IReadOnlyList<string> components)
    {
        string fullPath = Path.GetFullPath(Path.Combine(root, Path.Combine(components.ToArray())));
        string relative = Path.GetRelativePath(root, fullPath);
        if (Path.IsPathFullyQualified(relative)
            || relative.Equals("..", StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new MihomoServiceConfigurationTrustException(
                "A local provider escaped the authenticated configuration root.");
        }

        string current = root;
        foreach (string component in components.Take(components.Count - 1))
        {
            current = Path.Combine(current, component);
            EnsureOrdinaryDirectory(current, "local provider path component");
        }

        return fullPath;
    }

    private static void EnsureOrdinaryDirectory(string path, string description)
    {
        DirectoryInfo directory = new(path);
        directory.Refresh();
        if (!directory.Exists
            || (directory.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory))
                != FileAttributes.Directory)
        {
            throw new MihomoServiceConfigurationTrustException(
                $"The {description} is not an ordinary directory.");
        }
    }

    private static FileInfo RequireOrdinaryFile(string path, long maximumBytes, string description)
    {
        FileInfo file = new(path);
        file.Refresh();
        if (!file.Exists
            || file.Length is < 1
            || file.Length > maximumBytes
            || (file.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint
                | FileAttributes.Device)) != 0)
        {
            throw new MihomoServiceConfigurationTrustException(
                $"The {description} is missing or is not a bounded ordinary file.");
        }

        return file;
    }

    private static GeoRequirements DiscoverGeoRequirements(YamlMappingNode root)
    {
        bool geodataMode = TryGet(root, "geodata-mode", out YamlNode? modeNode)
            && modeNode is YamlScalarNode mode
            && IsTrue(mode.Value);
        bool geoIp = false;
        bool geoSite = false;
        bool asn = false;
        Stack<YamlNode> pending = new();
        pending.Push(root);
        HashSet<YamlNode> visited = new(ReferenceEqualityComparer.Instance);
        while (pending.TryPop(out YamlNode? node))
        {
            if (!visited.Add(node))
            {
                continue;
            }

            switch (node)
            {
                case YamlScalarNode { Value: string value }:
                    geoIp |= ContainsToken(value, "GEOIP")
                        || ContainsToken(value, "SRC-GEOIP")
                        || value.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase);
                    geoSite |= ContainsToken(value, "GEOSITE")
                        || value.StartsWith("geosite:", StringComparison.OrdinalIgnoreCase);
                    asn |= ContainsToken(value, "IP-ASN")
                        || ContainsToken(value, "SRC-IP-ASN");
                    break;
                case YamlMappingNode mapping:
                    foreach ((YamlNode key, YamlNode value) in mapping.Children)
                    {
                        pending.Push(key);
                        pending.Push(value);
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                    {
                        pending.Push(child);
                    }

                    break;
            }
        }

        if (TryGet(root, "dns", out YamlNode? dnsNode)
            && dnsNode is YamlMappingNode dns
            && TryGet(dns, "fallback", out YamlNode? fallbackNode)
            && fallbackNode is YamlSequenceNode { Children.Count: > 0 }
            && (!TryGet(dns, "fallback-filter", out YamlNode? filterNode)
                || filterNode is not YamlMappingNode filter
                || !TryGet(filter, "geoip", out YamlNode? geoIpNode)
                || geoIpNode is not YamlScalarNode geoIpScalar
                || !IsFalse(geoIpScalar.Value)))
        {
            geoIp = true;
        }

        return new GeoRequirements(geoIp, geoSite, asn, geodataMode);
    }

    private static bool ContainsToken(string value, string token)
    {
        int index = value.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            bool before = index == 0 || !char.IsLetterOrDigit(value[index - 1]);
            int afterIndex = index + token.Length;
            bool after = afterIndex == value.Length || !char.IsLetterOrDigit(value[afterIndex]);
            if (before && after)
            {
                return true;
            }

            index = value.IndexOf(token, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool TryGet(YamlMappingNode mapping, string key, out YamlNode? value)
    {
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is YamlScalarNode scalar
                && string.Equals(scalar.Value, key, StringComparison.Ordinal))
            {
                value = valueNode;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static string RequireMappingScalar(
        YamlMappingNode mapping,
        string key,
        string description)
    {
        return TryGet(mapping, key, out YamlNode? node)
            ? RequireScalar(node!, $"{description} '{key}'")
            : throw new MihomoServiceConfigurationTrustException(
                $"The {description} requires scalar '{key}'.");
    }

    private static string RequireScalar(YamlNode node, string description)
    {
        return node is YamlScalarNode { Value: not null } scalar
            && !string.IsNullOrWhiteSpace(scalar.Value)
                ? scalar.Value
                : throw new MihomoServiceConfigurationTrustException(
                    $"The {description} must be a nonempty scalar.");
    }

    private static void SetMappingScalar(YamlMappingNode mapping, string key, string value)
    {
        YamlNode? existingKey = mapping.Children.Keys.FirstOrDefault(candidate =>
            candidate is YamlScalarNode scalar
            && string.Equals(scalar.Value, key, StringComparison.Ordinal));
        YamlScalarNode scalarValue = new(value);
        if (existingKey is null)
        {
            mapping.Add(new YamlScalarNode(key), scalarValue);
        }
        else
        {
            mapping.Children[existingKey] = scalarValue;
        }
    }

    private static bool IsTrue(string? value) => value?.Trim() switch
    {
        "1" => true,
        string text when text.Equals("true", StringComparison.OrdinalIgnoreCase)
            || text.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || text.Equals("on", StringComparison.OrdinalIgnoreCase) => true,
        _ => false,
    };

    private static bool IsFalse(string? value) => value?.Trim() switch
    {
        "0" => true,
        string text when text.Equals("false", StringComparison.OrdinalIgnoreCase)
            || text.Equals("no", StringComparison.OrdinalIgnoreCase)
            || text.Equals("off", StringComparison.OrdinalIgnoreCase) => true,
        _ => false,
    };

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private sealed record GeoManifest
    {
        public int SchemaVersion { get; init; }

        public IReadOnlyList<GeoManifestEntry> Files { get; init; } = [];
    }

    private sealed record GeoManifestEntry
    {
        public string Name { get; init; } = string.Empty;

        public long Length { get; init; }

        public string Sha256 { get; init; } = string.Empty;
    }

    private readonly record struct GeoRequirements(
        bool GeoIp,
        bool GeoSite,
        bool Asn,
        bool GeodataMode)
    {
        internal bool Any => GeoIp || GeoSite || Asn;
    }
}
