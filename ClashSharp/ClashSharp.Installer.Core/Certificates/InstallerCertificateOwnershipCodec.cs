using System.Text.Json;
using System.Text.Json.Serialization;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Certificates;

/// <summary>Serializes and parses the strict bounded certificate-ownership document.</summary>
public static class InstallerCertificateOwnershipCodec
{
    /// <summary>Gets the maximum accepted UTF-8 document length.</summary>
    public const int MaximumDocumentBytes = 4096;

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private static readonly HashSet<string> RequiredProperties = new(StringComparer.Ordinal)
    {
        "schema",
        "ledgerId",
        "targetSid",
        "certificateThumbprint",
        "certificateSha256",
        "storeLocation",
        "storeName",
        "wasPreExisting",
        "installerOwned",
        "managedReferenceCount",
        "generation",
    };

    /// <summary>Serializes one validated ledger to canonical compact UTF-8 JSON.</summary>
    public static byte[] Serialize(InstallerCertificateOwnershipLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        ledger.Validate();
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(ledger, SerializerOptions);
        ValidateSize(bytes);
        return bytes;
    }

    /// <summary>Parses one strict, bounded UTF-8 JSON ownership ledger.</summary>
    public static InstallerCertificateOwnershipLedger Parse(ReadOnlySpan<byte> bytes)
    {
        ValidateSize(bytes);
        try
        {
            ValidateExactObjectShape(bytes);
            InstallerCertificateOwnershipLedger ledger =
                JsonSerializer.Deserialize<InstallerCertificateOwnershipLedger>(bytes, SerializerOptions)
                ?? throw new InstallerProtocolException("installer.certificate.json_invalid");
            ledger.Validate();
            return ledger;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException("installer.certificate.json_invalid", exception);
        }
    }

    private static void ValidateExactObjectShape(ReadOnlySpan<byte> bytes)
    {
        Utf8JsonReader reader = new(bytes, new JsonReaderOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 2,
        });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("The certificate ownership ledger must be one JSON object.");
        }

        HashSet<string> observed = new(StringComparer.Ordinal);
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.EndObject)
            {
                break;
            }

            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1)
            {
                throw new JsonException("Nested certificate ownership values are not allowed.");
            }

            string propertyName = reader.GetString()
                ?? throw new JsonException("A certificate ownership property name is invalid.");
            if (!RequiredProperties.Contains(propertyName) || !observed.Add(propertyName))
            {
                throw new JsonException("The certificate ownership ledger has an unknown or duplicate property.");
            }

            if (!reader.Read() || !HasExpectedTokenType(propertyName, reader.TokenType))
            {
                throw new JsonException("A certificate ownership property has an invalid value type.");
            }
        }

        if (reader.TokenType != JsonTokenType.EndObject
            || observed.Count != RequiredProperties.Count
            || reader.Read())
        {
            throw new JsonException("The certificate ownership property set is incomplete.");
        }
    }

    private static bool HasExpectedTokenType(string propertyName, JsonTokenType tokenType) =>
        propertyName switch
        {
            "schema" or "managedReferenceCount" or "generation" =>
                tokenType == JsonTokenType.Number,
            "wasPreExisting" or "installerOwned" =>
                tokenType is JsonTokenType.True or JsonTokenType.False,
            "ledgerId" or "targetSid" or "certificateThumbprint" or "certificateSha256"
                or "storeLocation" or "storeName" => tokenType == JsonTokenType.String,
            _ => false,
        };

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
        if (bytes.IsEmpty || bytes.Length > MaximumDocumentBytes)
        {
            throw new InstallerProtocolException("installer.certificate.size_invalid");
        }
    }
}
