using System;
using System.Collections.Generic;
using System.Linq;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Service;

/// <summary>Recognizes a mihomo configuration mapping before authoritative native validation.</summary>
/// <remarks>
/// Invariants: Validation is a conservative shape check and does not prove full YAML or mihomo correctness.
/// Thread safety: Stateless methods are safe for concurrent calls.
/// Side effects: None.
/// </remarks>
internal static class MihomoProfileShapeValidator
{
    private static readonly HashSet<string> ConfigurationKeys = new(StringComparer.Ordinal)
    {
        "proxies", "proxy-providers", "proxy-groups", "rules", "rule-providers", "dns", "tun", "listeners",
        "sniffer", "hosts", "profile", "geox-url", "geodata-mode", "mixed-port", "port", "socks-port",
    };
    /// <summary>Requires recognizable configuration content without inventing mandatory optional sections.</summary>
    /// <param name="configurationText">Configuration text. Must not be null.</param>
    /// <exception cref="ArgumentNullException"><paramref name="configurationText"/> is null.</exception>
    /// <exception cref="ArgumentException">The configuration does not look like a mihomo profile.</exception>
    public static void Validate(string configurationText)
    {
        ArgumentNullException.ThrowIfNull(configurationText);

        YamlMappingNode root = MihomoYamlSemanticValidator.LoadUniqueRootMappingNode(configurationText, nameof(configurationText));
        if (!root.Children.Keys.OfType<YamlScalarNode>().Any(key => key.Value is { } value && ConfigurationKeys.Contains(value)))
        {
            throw new ArgumentException("Expected a Clash/Mihomo configuration mapping.", nameof(configurationText));
        }
    }

}
