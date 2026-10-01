using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using ClashSharp.Model;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Service;

/// <summary>Projects DNS configuration without querying resolvers or exposing URL credentials.</summary>
internal static class RuntimeDnsConfigurationReader
{
    public static RuntimeDnsConfiguration? Read(string configurationText)
    {
        try
        {
            YamlMappingNode root = MihomoYamlSemanticValidator.LoadUniqueRootMappingNode(configurationText, nameof(configurationText));
            if (!root.Children.TryGetValue(new YamlScalarNode("dns"), out YamlNode? node))
            {
                return new(false, "redir-host", string.Empty, false, false, default, [], default, [], [], 0);
            }
            if (node is not YamlMappingNode mapping) { return null; }
            Dictionary<string, YamlNode> dns = new(StringComparer.Ordinal);
            foreach ((YamlNode key, YamlNode value) in mapping.Children)
            {
                if (key is not YamlScalarNode { Value: not null } scalar || !dns.TryAdd(scalar.Value, value)) { return null; }
            }
            string mode = Scalar(dns, "enhanced-mode", "redir-host");
            if (mode is not ("redir-host" or "fake-ip")) { return null; }
            int policies = 0;
            if (dns.TryGetValue("nameserver-policy", out YamlNode? policyNode))
            {
                if (policyNode is not YamlMappingNode policyMapping) { return null; }
                policies = policyMapping.Children.Count;
            }
            return new(Boolean(dns, "enable"), mode, Scalar(dns, "listen", string.Empty),
                Boolean(dns, "ipv6"), Boolean(dns, "respect-rules"),
                Servers(dns, "nameserver", inherit: true), Servers(dns, "fallback"),
                Servers(dns, "default-nameserver", inherit: true), Servers(dns, "direct-nameserver"),
                Servers(dns, "proxy-server-nameserver"), policies);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string Scalar(Dictionary<string, YamlNode> mapping, string key, string fallback)
    {
        if (!mapping.TryGetValue(key, out YamlNode? node)) { return fallback; }
        if (node is not YamlScalarNode { Value: not null } scalar || Array.Exists(scalar.Value.ToCharArray(), char.IsControl))
        {
            throw new ArgumentException("DNS value is not a displayable scalar.");
        }
        return scalar.Value;
    }

    private static bool Boolean(Dictionary<string, YamlNode> mapping, string key) =>
        Scalar(mapping, key, "false").ToLowerInvariant() switch
        {
            "true" or "yes" or "on" => true,
            "false" or "no" or "off" => false,
            _ => throw new ArgumentException("DNS value is not boolean."),
        };

    private static ImmutableArray<string> Servers(Dictionary<string, YamlNode> mapping, string key, bool inherit = false)
    {
        if (!mapping.TryGetValue(key, out YamlNode? node)) { return inherit ? default : []; }
        if (node is not YamlSequenceNode sequence) { throw new ArgumentException("DNS servers are not a sequence."); }
        var values = ImmutableArray.CreateBuilder<string>(sequence.Children.Count);
        foreach (YamlNode child in sequence.Children)
        {
            if (child is not YamlScalarNode { Value: not null } scalar || string.IsNullOrWhiteSpace(scalar.Value)
                || Array.Exists(scalar.Value.ToCharArray(), char.IsControl))
            {
                throw new ArgumentException("DNS server is not a displayable address.");
            }
            // The dashboard needs the resolver endpoint, never embedded authentication or routing parameters.
            string endpoint = scalar.Value.Split(['#', '?'], 2)[0];
            if (Uri.TryCreate(endpoint, UriKind.Absolute, out Uri? uri) && !string.IsNullOrEmpty(uri.Host))
            {
                UriBuilder display = new(uri) { UserName = string.Empty, Password = string.Empty, Path = string.Empty, Query = string.Empty, Fragment = string.Empty };
                endpoint = display.Uri.GetLeftPart(UriPartial.Authority);
            }
            values.Add(endpoint);
        }
        return values.MoveToImmutable();
    }
}
