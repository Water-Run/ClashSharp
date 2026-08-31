using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Payloads;

/// <summary>Serializes and parses the strict bounded embedded release manifest.</summary>
public static class InstallerReleaseManifestCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly HashSet<string> RequiredManifestProperties = new(StringComparer.Ordinal)
    {
        "schema",
        "expectedPackageVersion",
        "installerPayloadSha256",
        "authenticodeCertificateThumbprint",
        "packageCertificateThumbprint",
        "certificateSha256",
        "packageIdentity",
        "dependencies",
        "machineFiles",
        "files",
    };
    private static readonly HashSet<string> RequiredPackageIdentityProperties = new(StringComparer.Ordinal)
    {
        "name",
        "publisher",
        "publisherId",
        "architecture",
        "resourceId",
        "packageFullName",
        "packageFamilyName",
        "applicationId",
        "applicationExecutable",
        "applicationEntryPoint",
    };
    private static readonly HashSet<string> RequiredDependencyIdentityProperties = new(StringComparer.Ordinal)
    {
        "path",
        "name",
        "publisher",
        "publisherId",
        "version",
        "minimumVersion",
        "architecture",
        "resourceId",
        "packageFullName",
        "packageFamilyName",
    };
    private static readonly HashSet<string> RequiredFileProperties = new(StringComparer.Ordinal)
    {
        "path",
        "role",
        "length",
        "sha256",
    };
    private static readonly HashSet<string> RequiredMachineFileProperties = new(StringComparer.Ordinal)
    {
        "path",
        "length",
        "sha256",
    };

    /// <summary>Serializes one validated manifest to canonical compact UTF-8 JSON.</summary>
    public static byte[] Serialize(InstallerReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions);
        ValidateSize(bytes);
        return bytes;
    }

    /// <summary>Parses a strict manifest without accepting duplicate or unknown fields.</summary>
    public static InstallerReleaseManifest Parse(ReadOnlySpan<byte> bytes)
    {
        ValidateSize(bytes);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            ValidateExactShape(document.RootElement);
            InstallerReleaseManifest manifest =
                JsonSerializer.Deserialize<InstallerReleaseManifest>(bytes, SerializerOptions)
                ?? throw new JsonException("The installer release manifest is null.");
            manifest.Validate();
            return manifest;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException("installer.release.manifest_json_invalid", exception);
        }
    }

    private static void ValidateExactShape(JsonElement root)
    {
        ValidateObjectProperties(root, RequiredManifestProperties);
        foreach (JsonProperty property in root.EnumerateObject())
        {
            bool valid = property.Name switch
            {
                "schema" => property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt32(out _),
                "expectedPackageVersion" or "installerPayloadSha256"
                    or "authenticodeCertificateThumbprint"
                    or "packageCertificateThumbprint" or "certificateSha256" =>
                    property.Value.ValueKind == JsonValueKind.String,
                "packageIdentity" => property.Value.ValueKind == JsonValueKind.Object,
                "dependencies" or "machineFiles" or "files" =>
                    property.Value.ValueKind == JsonValueKind.Array,
                _ => false,
            };
            if (!valid)
            {
                throw new JsonException("An installer release manifest property has an invalid type.");
            }
        }

        ValidateStringObject(
            root.GetProperty("packageIdentity"),
            RequiredPackageIdentityProperties);
        foreach (JsonElement dependency in root.GetProperty("dependencies").EnumerateArray())
        {
            ValidateStringObject(dependency, RequiredDependencyIdentityProperties);
        }

        foreach (JsonElement machineFile in root.GetProperty("machineFiles").EnumerateArray())
        {
            ValidateObjectProperties(machineFile, RequiredMachineFileProperties);
            ValidateFilePropertyTypes(machineFile, hasRole: false);
        }

        JsonElement files = root.GetProperty("files");
        foreach (JsonElement file in files.EnumerateArray())
        {
            ValidateObjectProperties(file, RequiredFileProperties);
            ValidateFilePropertyTypes(file, hasRole: true);
        }
    }

    private static void ValidateFilePropertyTypes(JsonElement file, bool hasRole)
    {
        foreach (JsonProperty property in file.EnumerateObject())
        {
            bool valid = property.Name switch
            {
                "path" or "sha256" => property.Value.ValueKind == JsonValueKind.String,
                "role" => hasRole && property.Value.ValueKind == JsonValueKind.String,
                "length" => property.Value.ValueKind == JsonValueKind.Number
                    && property.Value.TryGetInt64(out _),
                _ => false,
            };
            if (!valid)
            {
                throw new JsonException(
                    "An installer release file property has an invalid type.");
            }
        }
    }

    private static void ValidateStringObject(
        JsonElement element,
        HashSet<string> requiredProperties)
    {
        ValidateObjectProperties(element, requiredProperties);
        if (element.EnumerateObject().Any(static property =>
            property.Value.ValueKind != JsonValueKind.String))
        {
            throw new JsonException("An installer release identity property has an invalid type.");
        }
    }

    private static void ValidateObjectProperties(
        JsonElement element,
        HashSet<string> requiredProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("An installer release value must be an object.");
        }

        var observed = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!requiredProperties.Contains(property.Name) || !observed.Add(property.Name))
            {
                throw new JsonException(
                    "The installer release manifest contains an unknown or duplicate property.");
            }
        }

        if (observed.Count != requiredProperties.Count)
        {
            throw new JsonException("The installer release manifest property set is incomplete.");
        }
    }

    private static JsonSerializerOptions CreateSerializerOptions() => new()
    {
        AllowTrailingCommas = false,
        PropertyNameCaseInsensitive = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) },
    };

    private static void ValidateSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > InstallerPayloadBudgets.MaximumManifestBytes)
        {
            throw new InstallerProtocolException("installer.release.manifest_size_invalid");
        }
    }
}
