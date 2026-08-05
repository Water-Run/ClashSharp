using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using ClashSharp.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Service;

/// <summary>Validates imported and generated mihomo YAML through a semantic document model.</summary>
/// <remarks>
/// Invariants: Runtime configuration is one root mapping with unique scalar keys, and every
/// Clash#-owned runtime field has the exact value selected by the application.
/// Thread safety: Stateless methods are safe for concurrent calls.
/// Side effects: None.
/// </remarks>
internal static class MihomoYamlSemanticValidator
{
    private static readonly HashSet<string> OwnedRuntimeKeys = new(StringComparer.Ordinal)
    {
        "mixed-port",
        "port",
        "socks-port",
        "redir-port",
        "tproxy-port",
        "ss-config",
        "vmess-config",
        "tuic-server",
        "tunnels",
        "allow-lan",
        "bind-address",
        "inbound-tfo",
        "inbound-mptcp",
        "authentication",
        "skip-auth-prefixes",
        "lan-allowed-ips",
        "lan-disallowed-ips",
        "listeners",
        "mode",
        "log-level",
        "tun",
        "external-controller",
        "external-controller-tls",
        "external-controller-pipe",
        "external-controller-unix",
        "external-controller-cors",
        "external-controller-routing-mark",
        "external-doh-server",
        "external-ui",
        "external-ui-name",
        "external-ui-url",
        "secret",
    };

    /// <summary>Returns whether one root RawConfig key is exclusively controlled by Clash#.</summary>
    public static bool IsAppOwnedRuntimeKey(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return OwnedRuntimeKeys.Contains(key);
    }

    /// <summary>Ensures imported text can be overlaid without ambiguous root YAML semantics.</summary>
    public static void ValidateOverlayInput(string configurationText)
    {
        _ = LoadUniqueRootMapping(configurationText, nameof(configurationText));
    }

    /// <summary>Reads owner-relevant activation values from a previously managed configuration.</summary>
    public static RuntimeConfigurationActivationPlan ReadActivationPlan(
        string configurationText,
        string profileId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);
        Dictionary<string, YamlNode> root = LoadUniqueRootMapping(configurationText, nameof(configurationText));
        int mixedPort = ReadPort(root, "mixed-port", nameof(configurationText));
        ClashSharpMode mode = ReadScalar(root, "mode", nameof(configurationText)) switch
        {
            "direct" => ClashSharpMode.Standby,
            "rule" => ClashSharpMode.RuleTakeover,
            "global" => ClashSharpMode.FullTakeover,
            _ => throw new ArgumentException(
                "Managed mihomo configuration contains an unsupported runtime mode.",
                nameof(configurationText)),
        };
        bool transparentProxyEnabled = false;
        if (root.TryGetValue("tun", out YamlNode? tunNode))
        {
            if (tunNode is not YamlMappingNode tunMapping)
            {
                throw new ArgumentException(
                    "Managed mihomo TUN configuration must be a mapping.",
                    nameof(configurationText));
            }

            Dictionary<string, YamlNode> tun = ReadUniqueScalarMapping(tunMapping, nameof(configurationText));
            transparentProxyEnabled = StringComparer.Ordinal.Equals(
                ReadScalar(tun, "enable", nameof(configurationText)),
                "true");
        }

        return new RuntimeConfigurationActivationPlan(
            mode,
            transparentProxyEnabled,
            mixedPort,
            profileId.Trim());
    }

    /// <summary>Ensures generated runtime text contains only the selected application-owned values.</summary>
    public static void ValidateManagedRuntimeConfiguration(
        string configurationText,
        int mixedPort,
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        string controllerSecret)
    {
        Dictionary<string, YamlNode> root = LoadUniqueRootMapping(configurationText, nameof(configurationText));
        string expectedMode = MihomoRuntimeConfigurationBuilder.MapToMihomoMode(mode);

        RequireScalar(root, "external-controller", MihomoControllerEndpoint.ListenAddress, nameof(configurationText));
        RequireScalar(root, "secret", controllerSecret, nameof(configurationText));
        RequireScalar(root, "allow-lan", "false", nameof(configurationText));
        RequireScalar(root, "bind-address", "127.0.0.1", nameof(configurationText));
        RequireScalar(root, "mode", expectedMode, nameof(configurationText));
        RequireScalar(root, "log-level", "info", nameof(configurationText));
        RequireScalar(
            root,
            "mixed-port",
            mixedPort.ToString(CultureInfo.InvariantCulture),
            nameof(configurationText));

        foreach (string forbiddenKey in OwnedRuntimeKeys)
        {
            if (forbiddenKey is "external-controller"
                or "secret"
                or "allow-lan"
                or "bind-address"
                or "mode"
                or "log-level"
                or "mixed-port"
                or "tun")
            {
                continue;
            }

            if (root.ContainsKey(forbiddenKey))
            {
                throw new ArgumentException(
                    $"Generated mihomo configuration retained application-owned key '{forbiddenKey}'.",
                    nameof(configurationText));
            }
        }

        if (!transparentProxyEnabled)
        {
            if (root.ContainsKey("tun"))
            {
                throw new ArgumentException(
                    "Generated mihomo configuration retained TUN while transparent proxy is disabled.",
                    nameof(configurationText));
            }

            return;
        }

        if (!root.TryGetValue("tun", out YamlNode? tunNode) || tunNode is not YamlMappingNode tunMapping)
        {
            throw new ArgumentException(
                "Generated mihomo configuration must contain a TUN mapping.",
                nameof(configurationText));
        }

        Dictionary<string, YamlNode> tun = ReadUniqueScalarMapping(tunMapping, nameof(configurationText));
        RequireScalar(tun, "enable", "true", nameof(configurationText));
        RequireScalar(tun, "stack", "system", nameof(configurationText));
        RequireScalar(tun, "auto-route", "true", nameof(configurationText));
        RequireScalar(tun, "auto-detect-interface", "true", nameof(configurationText));
        RequireScalar(tun, "strict-route", "false", nameof(configurationText));
        if (!tun.TryGetValue("dns-hijack", out YamlNode? dnsHijackNode)
            || dnsHijackNode is not YamlSequenceNode dnsHijack
            || dnsHijack.Children.Count != 1
            || dnsHijack.Children[0] is not YamlScalarNode dnsHijackValue
            || !StringComparer.Ordinal.Equals(dnsHijackValue.Value, "any:53"))
        {
            throw new ArgumentException(
                "Generated mihomo TUN configuration must contain only the managed DNS hijack endpoint.",
                nameof(configurationText));
        }

        if (!root.TryGetValue("dns", out YamlNode? dnsNode) || dnsNode is not YamlMappingNode dnsMapping)
        {
            throw new ArgumentException(
                "Transparent proxy requires an enabled mihomo DNS mapping.",
                nameof(configurationText));
        }

        Dictionary<string, YamlNode> dns = ReadUniqueScalarMapping(dnsMapping, nameof(configurationText));
        RequireScalar(dns, "enable", "true", nameof(configurationText));
        string enhancedMode = ReadScalar(dns, "enhanced-mode", nameof(configurationText));
        if (enhancedMode is not "fake-ip" and not "redir-host")
        {
            throw new ArgumentException(
                "Transparent proxy DNS enhanced-mode must be fake-ip or redir-host.",
                nameof(configurationText));
        }

        RequireNonEmptyScalarSequence(dns, "default-nameserver", nameof(configurationText));
        RequireNonEmptyScalarSequence(dns, "nameserver", nameof(configurationText));
    }

    /// <summary>Loads one unique scalar-keyed root mapping for semantic overlay.</summary>
    internal static YamlMappingNode LoadUniqueRootMappingNode(
        string configurationText,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(configurationText);

        YamlStream stream = new();
        try
        {
            using StringReader reader = new(configurationText);
            stream.Load(reader);
        }
        catch (YamlException exception)
        {
            throw new ArgumentException("Mihomo configuration is not valid YAML.", parameterName, exception);
        }

        if (stream.Documents.Count != 1)
        {
            throw new ArgumentException(
                "Mihomo configuration must contain exactly one YAML document.",
                parameterName);
        }

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new ArgumentException(
                "Mihomo configuration must use a root YAML mapping.",
                parameterName);
        }

        _ = ReadUniqueScalarMapping(root, parameterName);
        return root;
    }

    private static Dictionary<string, YamlNode> LoadUniqueRootMapping(
        string configurationText,
        string parameterName)
    {
        return ReadUniqueScalarMapping(
            LoadUniqueRootMappingNode(configurationText, parameterName),
            parameterName);
    }

    private static Dictionary<string, YamlNode> ReadUniqueScalarMapping(
        YamlMappingNode mapping,
        string parameterName)
    {
        Dictionary<string, YamlNode> result = new(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: not null } scalarKey)
            {
                throw new ArgumentException(
                    "Mihomo configuration mapping keys must be scalar values.",
                    parameterName);
            }

            string key = scalarKey.Value;
            if (StringComparer.Ordinal.Equals(key, "<<"))
            {
                throw new ArgumentException(
                    "Mihomo configuration cannot use YAML merge keys.",
                    parameterName);
            }

            if (!result.TryAdd(key, valueNode))
            {
                throw new ArgumentException(
                    $"Mihomo configuration contains duplicate mapping key '{key}'.",
                    parameterName);
            }
        }

        return result;
    }

    private static void RequireScalar(
        IReadOnlyDictionary<string, YamlNode> mapping,
        string key,
        string expectedValue,
        string parameterName)
    {
        if (!mapping.TryGetValue(key, out YamlNode? node)
            || node is not YamlScalarNode scalar
            || !StringComparer.Ordinal.Equals(scalar.Value, expectedValue))
        {
            throw new ArgumentException(
                $"Generated mihomo configuration has an unexpected value for application-owned key '{key}'.",
                parameterName);
        }
    }

    private static string ReadScalar(
        IReadOnlyDictionary<string, YamlNode> mapping,
        string key,
        string parameterName)
    {
        if (!mapping.TryGetValue(key, out YamlNode? node)
            || node is not YamlScalarNode { Value: not null } scalar)
        {
            throw new ArgumentException(
                $"Managed mihomo configuration is missing scalar key '{key}'.",
                parameterName);
        }

        return scalar.Value;
    }

    private static int ReadPort(
        IReadOnlyDictionary<string, YamlNode> mapping,
        string key,
        string parameterName)
    {
        string value = ReadScalar(mapping, key, parameterName);
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int port)
            || port is < 1 or > 65535)
        {
            throw new ArgumentException(
                $"Managed mihomo configuration contains an invalid '{key}'.",
                parameterName);
        }

        return port;
    }

    private static void RequireNonEmptyScalarSequence(
        IReadOnlyDictionary<string, YamlNode> mapping,
        string key,
        string parameterName)
    {
        if (!mapping.TryGetValue(key, out YamlNode? node)
            || node is not YamlSequenceNode { Children.Count: > 0 } sequence)
        {
            throw new ArgumentException(
                $"Transparent proxy DNS requires a non-empty '{key}' sequence.",
                parameterName);
        }

        foreach (YamlNode child in sequence.Children)
        {
            if (child is not YamlScalarNode { Value: { Length: > 0 } })
            {
                throw new ArgumentException(
                    $"Transparent proxy DNS '{key}' entries must be non-empty scalars.",
                    parameterName);
            }
        }
    }
}
