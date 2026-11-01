using System;
using System.Collections.Generic;
using ClashSharp.Model;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Service;

internal static partial class MihomoProfilePreviewParser
{
    private static bool TryReadSemanticRoot(string text, out YamlMappingNode root)
    {
        try { root = MihomoYamlSemanticValidator.LoadUniqueRootMappingNode(text, nameof(text)); return true; }
        catch (ArgumentException) { root = null!; return false; }
    }

    private static string Scalar(YamlMappingNode mapping, string key) =>
        mapping.Children.TryGetValue(new YamlScalarNode(key), out var value) && value is YamlScalarNode scalar
            ? scalar.Value ?? string.Empty : string.Empty;

    private static IReadOnlyList<ProxyNode> ParseSemanticNodes(YamlMappingNode root, Func<string, RegionMetadata> resolveRegion)
    {
        List<ProxyNode> nodes = [];
        if (root.Children.TryGetValue(new YamlScalarNode("proxies"), out var source) && source is YamlSequenceNode proxies)
        {
            foreach (var child in proxies.Children)
            {
                if (child is not YamlMappingNode fields || string.IsNullOrWhiteSpace(Scalar(fields, "name"))) { continue; }
                string name = Scalar(fields, "name");
                string protocol = Scalar(fields, "type");
                string host = Scalar(fields, "server");
                string region = InferRegionCode(name);
                if (region == "UN") { region = InferRegionCode(host); }
                nodes.Add(new(name, protocol.Length == 0 ? "UNKNOWN" : protocol.ToUpperInvariant(), resolveRegion(region),
                    null, host, TryParsePort(Scalar(fields, "port"))));
            }
        }
        if (root.Children.TryGetValue(new YamlScalarNode("proxy-providers"), out var providerSource) && providerSource is YamlMappingNode providers)
        {
            foreach (var (key, value) in providers.Children)
            {
                if (key is not YamlScalarNode { Value: { Length: > 0 } name } || value is not YamlMappingNode fields) { continue; }
                string protocol = Scalar(fields, "type");
                string host = TryGetHostFromUri(Scalar(fields, "url"));
                string region = InferRegionCode(name);
                if (region == "UN") { region = InferRegionCode(host); }
                nodes.Add(new(name, "PROVIDER/" + (protocol.Length == 0 ? "PROVIDER" : protocol.ToUpperInvariant()), resolveRegion(region), null, host));
            }
        }
        return nodes;
    }

    private static IReadOnlyList<RulePreview> ParseSemanticRules(YamlMappingNode root, string sourceName)
    {
        List<RulePreview> rules = [];
        if (!root.Children.TryGetValue(new YamlScalarNode("rules"), out var source) || source is not YamlSequenceNode sequence) { return rules; }
        foreach (var child in sequence.Children)
        {
            if (child is not YamlScalarNode { Value: { } raw } || string.IsNullOrWhiteSpace(raw)) { continue; }
            string[] parts = raw.Split(',', StringSplitOptions.TrimEntries);
            if (string.IsNullOrWhiteSpace(parts[0])) { continue; }
            rules.Add(new(sourceName, parts[0], parts.Length > 2 ? parts[1] : "*",
                parts.Length > 2 ? parts[2] : parts.Length > 1 ? parts[1] : "DIRECT", 0));
        }
        return rules;
    }
}
