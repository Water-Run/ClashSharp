using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Durable user choices, scoped to a profile rather than a particular mihomo process.</summary>
internal interface IProxySelectionStore
{
    IReadOnlyDictionary<string, string> Read(string profileId);

    void Save(string profileId, string groupName, string proxyName);
}

/// <summary>Reads bounded, uncached selection data so import and reset take effect immediately.</summary>
/// <remarks>Callers serialize writes with profile and network mutations through the application gate.</remarks>
internal sealed class ProxySelectionStore : IProxySelectionStore
{
    private const int MaximumFileBytes = 2 * 1024 * 1024;
    private const int MaximumProfiles = 1024;
    private readonly string _path;

    public ProxySelectionStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path))
        {
            throw new ArgumentException("The selection store path must be absolute.", nameof(path));
        }

        _path = Path.GetFullPath(path);
    }

    public IReadOnlyDictionary<string, string> Read(string profileId)
    {
        ValidateName(profileId);
        Dictionary<string, Dictionary<string, string>> profiles = ReadAll();
        return profiles.TryGetValue(profileId, out Dictionary<string, string>? selections)
            ? selections
            : new Dictionary<string, string>(StringComparer.Ordinal);
    }

    public void Save(string profileId, string groupName, string proxyName)
    {
        ValidateName(profileId);
        ValidateName(groupName);
        ValidateName(proxyName);
        Dictionary<string, Dictionary<string, string>> profiles = ReadAll();
        if (!profiles.TryGetValue(profileId, out Dictionary<string, string>? selections))
        {
            if (profiles.Count >= MaximumProfiles) { throw InvalidStore(); }
            selections = new Dictionary<string, string>(StringComparer.Ordinal);
            profiles.Add(profileId, selections);
        }

        if (!selections.ContainsKey(groupName)
            && selections.Count >= MihomoServiceIpcProtocol.MaximumControllerProxyGroups)
        {
            throw InvalidStore();
        }

        selections[groupName] = proxyName;
        string json = JsonSerializer.Serialize(new { version = 1, profiles });
        if (System.Text.Encoding.UTF8.GetByteCount(json) >= MaximumFileBytes) { throw InvalidStore(); }
        DurableAtomicFile.WriteText(_path, json);
    }

    private Dictionary<string, Dictionary<string, string>> ReadAll()
    {
        Dictionary<string, Dictionary<string, string>> profiles = new(StringComparer.Ordinal);
        if (!File.Exists(_path)) { return profiles; }
        using FileStream stream = File.OpenRead(_path);
        if (stream.Length is <= 0 or > MaximumFileBytes) { throw InvalidStore(); }
        byte[] bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        using JsonDocument document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 6 });
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || root.EnumerateObject().Count() != 2
            || !root.TryGetProperty("version", out JsonElement version)
            || version.ValueKind != JsonValueKind.Number
            || !version.TryGetInt32(out int schema) || schema != 1
            || !root.TryGetProperty("profiles", out JsonElement entries)
            || entries.ValueKind != JsonValueKind.Object)
        {
            throw InvalidStore();
        }

        foreach (JsonProperty profile in entries.EnumerateObject())
        {
            ValidateName(profile.Name);
            if (profiles.Count >= MaximumProfiles || profile.Value.ValueKind != JsonValueKind.Object)
            {
                throw InvalidStore();
            }

            Dictionary<string, string> selections = new(StringComparer.Ordinal);
            foreach (JsonProperty selection in profile.Value.EnumerateObject())
            {
                ValidateName(selection.Name);
                if (selection.Value.ValueKind != JsonValueKind.String) { throw InvalidStore(); }
                string proxyName = selection.Value.GetString()!;
                ValidateName(proxyName);
                if (selections.Count >= MihomoServiceIpcProtocol.MaximumControllerProxyGroups
                    || !selections.TryAdd(selection.Name, proxyName))
                {
                    throw InvalidStore();
                }
            }

            if (!profiles.TryAdd(profile.Name, selections)) { throw InvalidStore(); }
        }

        return profiles;
    }

    internal static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Length > MihomoServiceIpcProtocol.MaximumControllerIdentifierCharacters
            || name.Any(char.IsControl))
        {
            throw new ArgumentException("A proxy selection identifier is invalid.", nameof(name));
        }
    }

    private static InvalidDataException InvalidStore() => new("The proxy selection store is invalid or exceeds its limits.");
}
