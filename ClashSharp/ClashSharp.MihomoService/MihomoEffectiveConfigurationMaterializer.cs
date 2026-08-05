using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ClashSharp.ServiceProtocol;
using Microsoft.Extensions.DependencyInjection;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.MihomoService;

/// <summary>Service-private capability used to reach one mihomo controller instance.</summary>
internal sealed record MihomoControllerAuthority(string PipeName, string Secret)
{
    public override string ToString() => nameof(MihomoControllerAuthority);
}

/// <summary>A service-owned launch configuration derived from an immutable source generation.</summary>
internal sealed record MihomoEffectiveGeneration(
    MihomoStagedGeneration Source,
    string ConfigurationPath,
    string EffectiveHash,
    MihomoControllerAuthority Authority)
{
    public override string ToString() =>
        $"{nameof(MihomoEffectiveGeneration)} {{ Generation = {Source.Generation.ToString(CultureInfo.InvariantCulture)} }}";
}

/// <summary>
/// Materializes a pipe-only controller overlay without changing the App-owned source generation.
/// </summary>
/// <remarks>
/// One materialized instance must be cached for the lifetime of a supervisor generation so an
/// unexpected child restart reuses the same effective bytes. Deletion is deliberately explicit:
/// the caller must first prove that the child Job is empty.
/// </remarks>
internal sealed class MihomoEffectiveConfigurationMaterializer
{
    private const string EffectiveDirectoryName = "effective";
    private const int AuthorityByteCount = 32;
    private static readonly Encoding Utf8WithoutBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: false,
        throwOnInvalidBytes: true);
    private static readonly HashSet<string> ControllerKeys = new(StringComparer.Ordinal)
    {
        "external-controller",
        "external-controller-tls",
        "external-controller-unix",
        "external-controller-pipe",
        "external-controller-cors",
        "mixed-port",
        "secret",
    };

    private readonly bool _protectDirectory;
    private readonly MihomoRuntimeAssetStager _assetStager;
    private readonly object _ownedPathsGate = new();
    private readonly HashSet<string> _ownedPaths = new(StringComparer.OrdinalIgnoreCase);

    internal MihomoEffectiveConfigurationMaterializer(bool protectDirectory = true)
        : this(geoDataDirectory: null, protectDirectory)
    {
    }

    internal MihomoEffectiveConfigurationMaterializer(
        string? geoDataDirectory,
        bool protectDirectory)
        : this(geoDataDirectory, sourceAssetRoot: null, protectDirectory)
    {
    }

    internal MihomoEffectiveConfigurationMaterializer(
        string? geoDataDirectory,
        string? sourceAssetRoot,
        bool protectDirectory)
    {
        _protectDirectory = protectDirectory;
        _assetStager = new MihomoRuntimeAssetStager(
            geoDataDirectory,
            sourceAssetRoot,
            protectDirectory);
    }

    [ActivatorUtilitiesConstructor]
    internal MihomoEffectiveConfigurationMaterializer(MihomoServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _protectDirectory = true;
        _assetStager = new MihomoRuntimeAssetStager(
            options.GeoDataDirectory,
            Path.GetDirectoryName(options.ConfigPath),
            protectDirectory: true);
    }

    internal async Task<MihomoEffectiveGeneration> MaterializeAsync(
        MihomoStagedGeneration source,
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        ArgumentOutOfRangeException.ThrowIfLessThan(source.Generation, 1);
        if (!MihomoServiceIpcProtocol.IsCanonicalSha256(source.ConfigurationHash))
        {
            throw new ArgumentException("The source configuration hash is invalid.", nameof(source));
        }

        byte[] sourceBytes = await File.ReadAllBytesAsync(source.ConfigurationPath, cancellationToken)
            .ConfigureAwait(false);
        string actualSourceHash = ComputeHash(sourceBytes);
        if (!CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(actualSourceHash),
                Convert.FromHexString(source.ConfigurationHash)))
        {
            throw new MihomoConfigurationHashMismatchException();
        }

        YamlMappingNode root = LoadRootMapping(Utf8WithoutBom.GetString(sourceBytes));
        await _assetStager.PrepareAsync(
                root,
                source.ConfigurationPath,
                runtimeDirectory,
                cancellationToken)
            .ConfigureAwait(false);
        RemoveRootControllerKeys(root);

        string pipeSuffix = CreateRandomHex();
        string secret = CreateRandomHex();
        string pipeName = $@"\\.\pipe\ClashSharp.Mihomo.Controller.{pipeSuffix}";
        root.Add(PlainScalar("external-controller-pipe"), QuotedScalar(pipeName));
        root.Add(PlainScalar("secret"), QuotedScalar(secret));
        root.Add(PlainScalar("mixed-port"), PlainScalar("0"));

        byte[] effectiveBytes = Serialize(root);
        string effectiveHash = ComputeHash(effectiveBytes);
        string effectiveDirectory = PrepareEffectiveDirectory(runtimeDirectory);
        string finalPath = Path.Combine(
            effectiveDirectory,
            $"effective-{source.Generation:D20}-{source.ConfigurationHash}-{effectiveHash}.yaml");
        string temporaryPath = Path.Combine(
            effectiveDirectory,
            $".{Guid.NewGuid():N}.tmp");
        bool movedToFinalPath = false;

        try
        {
            await WriteNewFileAsync(temporaryPath, effectiveBytes, cancellationToken)
                .ConfigureAwait(false);
            SecureFile(temporaryPath);
            File.Move(temporaryPath, finalPath);
            movedToFinalPath = true;
            SecureFile(finalPath);
            lock (_ownedPathsGate)
            {
                _ownedPaths.Add(Path.GetFullPath(finalPath));
            }
        }
        catch
        {
            TryDelete(temporaryPath);
            if (movedToFinalPath)
            {
                TryDelete(finalPath);
            }

            throw;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(sourceBytes);
            CryptographicOperations.ZeroMemory(effectiveBytes);
        }

        return new MihomoEffectiveGeneration(
            source,
            finalPath,
            effectiveHash,
            new MihomoControllerAuthority(pipeName, secret));
    }

    /// <summary>
    /// Deletes a materialized configuration after the caller has proved that the child Job is empty.
    /// </summary>
    internal void DeleteAfterJobEmpty(MihomoEffectiveGeneration effective)
    {
        ArgumentNullException.ThrowIfNull(effective);
        string fullPath = Path.GetFullPath(effective.ConfigurationPath);
        lock (_ownedPathsGate)
        {
            if (!_ownedPaths.Remove(fullPath))
            {
                throw new InvalidOperationException(
                    "The effective configuration is not owned by this materializer instance.");
            }
        }

        try
        {
            FileInfo file = new(fullPath);
            file.Refresh();
            if (file.Exists)
            {
                MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
                    file.Attributes,
                    "effective configuration");
                file.Attributes &= ~FileAttributes.ReadOnly;
                file.Delete();
            }
        }
        catch
        {
            lock (_ownedPathsGate)
            {
                _ownedPaths.Add(fullPath);
            }

            throw;
        }
    }

    /// <summary>
    /// Removes artifacts left by an earlier service process after the new supervisor has confirmed
    /// that it owns no Job. The installed SCM service is single-instance; unknown files are left
    /// untouched and cause no broad directory cleanup.
    /// </summary>
    internal void CleanupStaleAfterConfirmedNoOwnedJob(string runtimeDirectory)
    {
        string effectiveDirectoryPath = PrepareEffectiveDirectory(runtimeDirectory);
        DirectoryInfo effectiveDirectory = new(effectiveDirectoryPath);
        foreach (FileInfo file in effectiveDirectory.EnumerateFiles())
        {
            bool isOwnedName = IsEffectiveFileName(file.Name)
                || file.Name.Length == 37
                && file.Name[0] == '.'
                && file.Name.EndsWith(".tmp", StringComparison.Ordinal)
                && Guid.TryParseExact(file.Name.AsSpan(1, 32), "N", out _);
            if (!isOwnedName)
            {
                continue;
            }

            file.Refresh();
            MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
                file.Attributes,
                "stale effective configuration");
            file.Attributes &= ~FileAttributes.ReadOnly;
            file.Delete();
        }
    }

    private static bool IsEffectiveFileName(string name)
    {
        const string prefix = "effective-";
        const string suffix = ".yaml";
        const int generationLength = 20;
        const int hashLength = 64;
        const int expectedLength = 165;
        if (name.Length != expectedLength
            || !name.StartsWith(prefix, StringComparison.Ordinal)
            || !name.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> generation = name.AsSpan(prefix.Length, generationLength);
        int sourceHashStart = prefix.Length + generationLength + 1;
        int effectiveHashStart = sourceHashStart + hashLength + 1;
        return IsDecimalDigits(generation)
            && name[prefix.Length + generationLength] == '-'
            && name[sourceHashStart + hashLength] == '-'
            && MihomoServiceIpcProtocol.IsCanonicalSha256(
                name.Substring(sourceHashStart, hashLength))
            && MihomoServiceIpcProtocol.IsCanonicalSha256(
                name.Substring(effectiveHashStart, hashLength));
    }

    private static bool IsDecimalDigits(ReadOnlySpan<char> value)
    {
        foreach (char character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }

    private string PrepareEffectiveDirectory(string runtimeDirectory)
    {
        string runtimePath = Path.GetFullPath(runtimeDirectory);
        DirectoryInfo runtime = new(runtimePath);
        runtime.Refresh();
        if (!runtime.Exists)
        {
            throw new DirectoryNotFoundException("The protected runtime directory does not exist.");
        }

        MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
            runtime.Attributes,
            "runtime directory");
        DirectoryInfo effectiveDirectory = new(Path.Combine(runtimePath, EffectiveDirectoryName));
        effectiveDirectory.Refresh();
        if (effectiveDirectory.Exists)
        {
            MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
                effectiveDirectory.Attributes,
                "effective configuration directory");
        }
        else
        {
            effectiveDirectory.Create();
            effectiveDirectory.Refresh();
            MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
                effectiveDirectory.Attributes,
                "effective configuration directory");
        }

        if (_protectDirectory)
        {
            effectiveDirectory.SetAccessControl(
                MihomoGenerationStore.CreateProtectedDirectorySecurity());
        }

        return effectiveDirectory.FullName;
    }

    private void SecureFile(string path)
    {
        FileInfo file = new(path);
        file.Refresh();
        MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
            file.Attributes,
            "effective configuration");
        if (_protectDirectory)
        {
            file.SetAccessControl(MihomoGenerationStore.CreateProtectedFileSecurity());
            file.Refresh();
            MihomoGenerationStore.ValidateOwnedDirectoryAttributes(
                file.Attributes,
                "effective configuration");
        }

        file.Attributes |= FileAttributes.ReadOnly;
    }

    private static YamlMappingNode LoadRootMapping(string configurationText)
    {
        try
        {
            YamlStream stream = new();
            using StringReader reader = new(configurationText);
            stream.Load(reader);
            if (stream.Documents.Count != 1
                || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                throw new InvalidDataException(
                    "The source configuration must contain exactly one root mapping.");
            }

            return mapping;
        }
        catch (YamlException exception)
        {
            throw new InvalidDataException("The source configuration is invalid YAML.", exception);
        }
    }

    private static void RemoveRootControllerKeys(YamlMappingNode root)
    {
        YamlNode[] keysToRemove = root.Children.Keys
            .Where(key => key is YamlScalarNode scalar
                && scalar.Value is not null
                && ControllerKeys.Contains(scalar.Value))
            .ToArray();
        foreach (YamlNode key in keysToRemove)
        {
            root.Children.Remove(key);
        }
    }

    private static byte[] Serialize(YamlMappingNode root)
    {
        YamlStream stream = new(new YamlDocument(root));
        using StringWriter writer = new(CultureInfo.InvariantCulture);
        stream.Save(writer, assignAnchors: true);
        string normalized = writer.ToString()
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .TrimEnd() + "\n";
        return Utf8WithoutBom.GetBytes(normalized);
    }

    private static async Task WriteNewFileAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static YamlScalarNode PlainScalar(string value) => new(value)
    {
        Style = ScalarStyle.Plain,
    };

    private static YamlScalarNode QuotedScalar(string value) => new(value)
    {
        Style = ScalarStyle.SingleQuoted,
    };

    private static string CreateRandomHex() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(AuthorityByteCount)).ToLowerInvariant();

    private static string ComputeHash(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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
}
