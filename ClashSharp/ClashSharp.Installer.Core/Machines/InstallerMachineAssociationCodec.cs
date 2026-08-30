using System.Buffers;
using System.Security.Cryptography;
using System.Text.Json;
using ClashSharp.Installer.Contracts;

namespace ClashSharp.Installer.Machines;

/// <summary>Reads and writes the bounded exact association shared with the installed app.</summary>
public static class InstallerMachineAssociationCodec
{
    /// <summary>Maximum encoded association length accepted by the app and installer.</summary>
    public const int MaximumAssociationBytes = 4 * 1024;

    private static readonly string[] RequiredProperties =
    [
        "schemaVersion",
        "ownerSid",
        "authenticationToken",
    ];

    /// <summary>Serializes a validated association to canonical compact UTF-8 without a BOM.</summary>
    public static byte[] Serialize(InstallerMachineAssociation association)
    {
        ArgumentNullException.ThrowIfNull(association);
        association.Validate();
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", association.SchemaVersion);
            writer.WriteString("ownerSid", association.OwnerSid);
            writer.WriteString("authenticationToken", association.AuthenticationToken);
            writer.WriteEndObject();
        }

        byte[] bytes = buffer.WrittenSpan.ToArray();
        ValidateSize(bytes);
        return bytes;
    }

    /// <summary>Parses a canonical association, rejecting altered shape or encoding.</summary>
    public static InstallerMachineAssociation Parse(ReadOnlySpan<byte> bytes)
    {
        ValidateSize(bytes);
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 2,
            });
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new JsonException("The machine association must be an object.");
            }

            int propertyIndex = 0;
            var observed = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (propertyIndex >= RequiredProperties.Length
                    || !string.Equals(
                        property.Name,
                        RequiredProperties[propertyIndex],
                        StringComparison.Ordinal)
                    || !observed.Add(property.Name))
                {
                    throw new JsonException(
                        "The machine association has an unknown, duplicate, or reordered property.");
                }

                bool validType = property.Name switch
                {
                    "schemaVersion" => property.Value.ValueKind == JsonValueKind.Number
                        && property.Value.TryGetInt32(out _),
                    "ownerSid" or "authenticationToken" =>
                        property.Value.ValueKind == JsonValueKind.String,
                    _ => false,
                };
                if (!validType)
                {
                    throw new JsonException(
                        "A machine association property has an invalid type.");
                }

                propertyIndex++;
            }

            if (propertyIndex != RequiredProperties.Length
                || !observed.SetEquals(RequiredProperties))
            {
                throw new JsonException("The machine association property set is incomplete.");
            }

            InstallerMachineAssociation association = new(
                root.GetProperty("schemaVersion").GetInt32(),
                root.GetProperty("ownerSid").GetString()
                    ?? throw new JsonException("The machine owner SID is null."),
                root.GetProperty("authenticationToken").GetString()
                    ?? throw new JsonException("The machine authentication token is null."));
            association.Validate();
            byte[] canonical = Serialize(association);
            if (!CryptographicOperations.FixedTimeEquals(bytes, canonical))
            {
                throw new JsonException("The machine association is not canonical UTF-8 JSON.");
            }

            return association;
        }
        catch (JsonException exception)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_json_invalid",
                exception);
        }
    }

    private static void ValidateSize(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes.Length > MaximumAssociationBytes)
        {
            throw new InstallerProtocolException(
                "installer.machine.association_size_invalid");
        }
    }
}
