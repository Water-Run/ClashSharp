using System.Text.Json.Serialization;

namespace ClashSharp.Installer.Ownership;

// This is a separate private schema. Its nested continuation uses generated property names and
// string enums; only the existing v2 codec may write that continuation to the public journal file.
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    MaxDepth = 4)]
[JsonSerializable(typeof(InstallerOwnerTransferJournal))]
internal sealed partial class InstallerOwnerTransferJsonContext : JsonSerializerContext;
