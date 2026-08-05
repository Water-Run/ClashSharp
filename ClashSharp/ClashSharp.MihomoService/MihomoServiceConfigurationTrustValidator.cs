using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using ClashSharp.ServiceProtocol;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.MihomoService;

internal class MihomoServiceConfigurationTrustException : IOException
{
    internal MihomoServiceConfigurationTrustException(string message)
        : base(message)
    {
    }

    internal MihomoServiceConfigurationTrustException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

internal sealed class MihomoRuntimeAssetException : MihomoServiceConfigurationTrustException
{
    internal MihomoRuntimeAssetException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    internal string ErrorCode { get; }
}

/// <summary>Validates LocalSystem-visible paths in an exact staged mihomo configuration.</summary>
/// <remarks>
/// This is deliberately independent of App-side profile validation. The Windows service treats
/// every configuration byte supplied by the interactive user as untrusted, even when its hash is exact.
/// </remarks>
internal static class MihomoServiceConfigurationTrustValidator
{
    private const long MaximumConfigurationBytes = 8L * 1024 * 1024;

    private const int MaximumYamlNodeCount = 100_000;

    private const string RequiredControllerEndpoint = "127.0.0.1:9090";

    private static readonly HashSet<string> ForbiddenExternalUiKeys = new(StringComparer.Ordinal)
    {
        "external-ui",
        "external-ui-name",
        "external-ui-url",
    };

    private static readonly HashSet<string> ForbiddenServiceRootKeys = new(StringComparer.Ordinal)
    {
        "authentication",
        "external-controller-routing-mark",
        "external-doh-server",
        "geox-url",
        "inbound-mptcp",
        "inbound-tfo",
        "lan-allowed-ips",
        "lan-disallowed-ips",
        "listeners",
        "port",
        "redir-port",
        "skip-auth-prefixes",
        "socks-port",
        "ss-config",
        "tproxy-port",
        "tuic-server",
        "tunnels",
        "vmess-config",
    };

    private static readonly HashSet<string> RequiredTunKeys = new(StringComparer.Ordinal)
    {
        "enable",
        "stack",
        "auto-route",
        "auto-detect-interface",
        "strict-route",
        "dns-hijack",
    };

    private static readonly HashSet<string> ProviderSectionKeys = new(StringComparer.Ordinal)
    {
        "proxy-providers",
        "rule-providers",
    };

    private static readonly string[] GeodataRuleTypes =
    [
        "GEOIP",
        "SRC-GEOIP",
        "GEOSITE",
        "IP-ASN",
        "SRC-IP-ASN",
    ];

    private static readonly HashSet<string> ForbiddenControllerKeys = new(StringComparer.Ordinal)
    {
        "external-controller-cors",
        "external-controller-pipe",
        "external-controller-tls",
        "external-controller-unix",
    };

    private static readonly HashSet<string> ForbiddenLocalCredentialKeys = new(StringComparer.Ordinal)
    {
        "ca-file",
        "cert-file",
        "certificate",
        "client-cert",
        "client-certificate",
        "client-key",
        "identity-file",
        "key-file",
        "private-key-path",
        "ssh-key",
    };

    private static readonly SearchValues<char> WindowsInvalidFileNameCharacters =
        SearchValues.Create(
        [
        '<', '>', ':', '"', '|', '?', '*', '\0',
        ]);

    internal static async Task ValidateAsync(
        string configurationPath,
        string runtimeDirectory,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configurationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        ValidateRuntimeDirectory(runtimeDirectory);

        FileInfo configuration = new(configurationPath);
        configuration.Refresh();
        if (!configuration.Exists)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The staged mihomo configuration does not exist.");
        }

        if (configuration.Length > MaximumConfigurationBytes)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The staged mihomo configuration exceeds the service safety limit.");
        }

        string configurationText;
        try
        {
            await using FileStream stream = new(
                configuration.FullName,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using StreamReader reader = new(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024,
                leaveOpen: false);
            configurationText = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DecoderFallbackException exception)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The staged mihomo configuration is not valid UTF-8.",
                exception);
        }

        ValidateText(configurationText, runtimeDirectory);
    }

    internal static void ValidateText(string configurationText, string runtimeDirectory)
    {
        ArgumentNullException.ThrowIfNull(configurationText);
        _ = ValidateRuntimeDirectory(runtimeDirectory);
        YamlMappingNode root = LoadUniqueRoot(configurationText);
        RejectMergeKeys(root);
        RejectKnownLocalSystemSurfaces(root);

        Dictionary<string, YamlNode> rootItems = ReadUniqueMapping(root, "root mapping");
        ValidateRootControllerAuthority(rootItems);
        ValidateManagedServiceAuthority(rootItems);
        foreach (string forbiddenKey in ForbiddenExternalUiKeys)
        {
            if (rootItems.ContainsKey(forbiddenKey))
            {
                throw new MihomoServiceConfigurationTrustException(
                    $"The staged mihomo configuration contains forbidden key '{forbiddenKey}'.");
            }
        }


        foreach (string forbiddenKey in ForbiddenServiceRootKeys)
        {
            if (rootItems.ContainsKey(forbiddenKey))
            {
                throw new MihomoServiceConfigurationTrustException(
                    $"The staged mihomo configuration contains forbidden service root key '{forbiddenKey}'.");
            }
        }

        RejectDnsListener(rootItems);

        foreach (string sectionKey in ProviderSectionKeys)
        {
            if (!rootItems.TryGetValue(sectionKey, out YamlNode? sectionNode))
            {
                continue;
            }

            if (sectionNode is not YamlMappingNode providers)
            {
                throw new MihomoServiceConfigurationTrustException(
                    $"The '{sectionKey}' section must be a mapping.");
            }

            Dictionary<string, YamlNode> providerItems = ReadUniqueMapping(
                providers,
                $"'{sectionKey}' section");
            if (providerItems.Count > MihomoServiceIpcProtocol.MaximumControllerProviders)
            {
                throw new MihomoServiceConfigurationTrustException(
                    $"The '{sectionKey}' section exceeds the provider safety limit.");
            }

            foreach ((string providerName, YamlNode providerNode) in providerItems)
            {
                if (string.IsNullOrWhiteSpace(providerName)
                    || providerName.Length
                        > MihomoServiceIpcProtocol.MaximumControllerIdentifierCharacters
                    || providerName.Any(char.IsControl))
                {
                    throw new MihomoServiceConfigurationTrustException(
                        "Provider names must be bounded non-control text.");
                }

                if (providerNode is not YamlMappingNode provider)
                {
                    throw new MihomoServiceConfigurationTrustException(
                        $"Provider '{providerName}' must be a mapping.");
                }

                Dictionary<string, YamlNode> fields = ReadUniqueMapping(
                    provider,
                    $"provider '{providerName}'");
                string providerType = ReadRequiredScalar(
                    fields,
                    "type",
                    $"provider '{providerName}'");
                if (providerType.Equals("inline", StringComparison.OrdinalIgnoreCase))
                {
                    if (fields.ContainsKey("path"))
                    {
                        throw new MihomoServiceConfigurationTrustException(
                            $"Inline provider '{providerName}' cannot select a local path.");
                    }

                    continue;
                }

                if (providerType.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    string url = ReadRequiredScalar(fields, "url", $"provider '{providerName}'");
                    ValidateHttpProviderUrl(providerName, url);
                    if (fields.TryGetValue("path", out YamlNode? httpPathNode))
                    {
                        ValidateProviderRelativePath(
                            ReadScalar(httpPathNode, $"provider '{providerName}' path"));
                    }

                    continue;
                }

                if (providerType.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    if (fields.ContainsKey("url"))
                    {
                        throw new MihomoServiceConfigurationTrustException(
                            $"File provider '{providerName}' cannot select a remote URL.");
                    }

                    ValidateProviderRelativePath(
                        ReadRequiredScalar(fields, "path", $"provider '{providerName}'"));
                    continue;
                }

                throw new MihomoServiceConfigurationTrustException(
                    $"Provider '{providerName}' has unsupported type '{providerType}'.");
            }
        }
    }

    private static void ValidateHttpProviderUrl(string providerName, string url)
    {
        if (url.Length > 4096
            || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            || uri.Scheme is not ("http" or "https")
            || string.IsNullOrWhiteSpace(uri.Host)
            || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new MihomoServiceConfigurationTrustException(
                $"HTTP provider '{providerName}' must use an absolute http/https URL without userinfo.");
        }
    }

    private static void RejectUnstagedGeodataConsumers(
        IReadOnlyDictionary<string, YamlNode> rootItems)
    {
        if (rootItems.TryGetValue("rules", out YamlNode? rulesNode))
        {
            RejectGeodataRuleSequence(rulesNode, "'rules' section");
        }

        if (rootItems.TryGetValue("sub-rules", out YamlNode? subRulesNode)
            && subRulesNode is YamlMappingNode subRules)
        {
            foreach ((string subRuleName, YamlNode subRuleNode) in ReadUniqueMapping(
                         subRules,
                         "'sub-rules' section"))
            {
                RejectGeodataRuleSequence(
                    subRuleNode,
                    $"sub-rule '{subRuleName}'");
            }
        }

        if (rootItems.TryGetValue("dns", out YamlNode? dnsNode)
            && dnsNode is YamlMappingNode dns)
        {
            RejectDnsGeodataConsumers(ReadUniqueMapping(dns, "'dns' section"));
        }

        if (rootItems.TryGetValue("sniffer", out YamlNode? snifferNode)
            && snifferNode is YamlMappingNode sniffer)
        {
            Dictionary<string, YamlNode> fields = ReadUniqueMapping(
                sniffer,
                "'sniffer' section");
            RejectPrefixedScalarSequence(fields, "force-domain", "geosite:");
            RejectPrefixedScalarSequence(fields, "skip-domain", "geosite:");
            RejectPrefixedScalarSequence(fields, "skip-src-address", "geoip:");
            RejectPrefixedScalarSequence(fields, "skip-dst-address", "geoip:");
        }
    }

    private static void RejectDnsGeodataConsumers(
        IReadOnlyDictionary<string, YamlNode> fields)
    {
        RejectGeositePolicyKeys(fields, "nameserver-policy");
        RejectGeositePolicyKeys(fields, "proxy-server-nameserver-policy");

        if (fields.TryGetValue("enhanced-mode", out YamlNode? enhancedModeNode)
            && enhancedModeNode is YamlScalarNode enhancedMode
            && enhancedMode.Value?.Equals("fake-ip", StringComparison.OrdinalIgnoreCase) == true
            && fields.TryGetValue("fake-ip-filter", out YamlNode? fakeIpFilterNode)
            && fakeIpFilterNode is YamlSequenceNode fakeIpFilter)
        {
            foreach (YamlNode item in fakeIpFilter.Children)
            {
                if (item is YamlScalarNode { Value: string value }
                    && (StartsWithGeodataReference(value, "geosite:")
                        || ContainsGeodataRuleExpression(value)))
                {
                    ThrowUnstagedGeodataConsumer("'dns.fake-ip-filter'");
                }
            }
        }

        if (!fields.TryGetValue("fallback", out YamlNode? fallbackNode)
            || fallbackNode is not YamlSequenceNode { Children.Count: > 0 })
        {
            return;
        }

        if (!fields.TryGetValue("fallback-filter", out YamlNode? fallbackFilterNode))
        {
            throw CreateUnstagedGeodataConsumerException(
                "'dns.fallback' with its default GeoIP filter");
        }

        YamlMappingNode fallbackFilter = fallbackFilterNode as YamlMappingNode
            ?? throw CreateUnstagedGeodataConsumerException("'dns.fallback-filter'");

        Dictionary<string, YamlNode> fallbackFields = ReadUniqueMapping(
            fallbackFilter,
            "'dns.fallback-filter' section");
        if (!fallbackFields.TryGetValue("geoip", out YamlNode? geoIpNode)
            || !IsExplicitFalseScalar(geoIpNode))
        {
            ThrowUnstagedGeodataConsumer("'dns.fallback-filter.geoip'");
        }

        if (fallbackFields.TryGetValue("geosite", out YamlNode? geoSiteNode)
            && geoSiteNode is YamlSequenceNode { Children.Count: > 0 })
        {
            ThrowUnstagedGeodataConsumer("'dns.fallback-filter.geosite'");
        }
    }

    private static void RejectGeositePolicyKeys(
        IReadOnlyDictionary<string, YamlNode> dnsFields,
        string policyKey)
    {
        if (!dnsFields.TryGetValue(policyKey, out YamlNode? policyNode)
            || policyNode is not YamlMappingNode policy)
        {
            return;
        }

        foreach (YamlNode keyNode in policy.Children.Keys)
        {
            if (keyNode is YamlScalarNode { Value: string key }
                && key.Split(',').Any(segment =>
                    StartsWithGeodataReference(segment, "geosite:")))
            {
                ThrowUnstagedGeodataConsumer($"'dns.{policyKey}'");
            }
        }
    }

    private static void RejectInlineRuleProviderGeodata(
        string providerName,
        IReadOnlyDictionary<string, YamlNode> fields)
    {
        if (!fields.TryGetValue("behavior", out YamlNode? behaviorNode)
            || behaviorNode is not YamlScalarNode behavior
            || !string.Equals(behavior.Value, "classical", StringComparison.OrdinalIgnoreCase)
            || !fields.TryGetValue("payload", out YamlNode? payloadNode))
        {
            return;
        }

        RejectGeodataRuleSequence(payloadNode, $"rule provider '{providerName}'");
    }

    private static void RejectGeodataRuleSequence(YamlNode node, string description)
    {
        if (node is not YamlSequenceNode sequence)
        {
            return;
        }

        foreach (YamlNode item in sequence.Children)
        {
            if (item is YamlScalarNode { Value: string rule }
                && ContainsGeodataRuleExpression(rule))
            {
                ThrowUnstagedGeodataConsumer(description);
            }
        }
    }

    private static void RejectPrefixedScalarSequence(
        IReadOnlyDictionary<string, YamlNode> fields,
        string key,
        string prefix)
    {
        if (!fields.TryGetValue(key, out YamlNode? node)
            || node is not YamlSequenceNode sequence)
        {
            return;
        }

        foreach (YamlNode item in sequence.Children)
        {
            if (item is YamlScalarNode { Value: string value }
                && StartsWithGeodataReference(value, prefix))
            {
                ThrowUnstagedGeodataConsumer($"'sniffer.{key}'");
            }
        }
    }

    private static bool StartsWithGeodataReference(string value, string prefix)
    {
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsGeodataRuleExpression(string rule)
    {
        int candidateOffset = 0;
        while (candidateOffset <= rule.Length)
        {
            int typeOffset = candidateOffset;
            while (typeOffset < rule.Length && char.IsWhiteSpace(rule[typeOffset]))
            {
                typeOffset++;
            }

            foreach (string ruleType in GeodataRuleTypes)
            {
                if (!rule.AsSpan(typeOffset).StartsWith(ruleType, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                int separatorOffset = typeOffset + ruleType.Length;
                while (separatorOffset < rule.Length && char.IsWhiteSpace(rule[separatorOffset]))
                {
                    separatorOffset++;
                }

                if (separatorOffset < rule.Length && rule[separatorOffset] == ',')
                {
                    return true;
                }
            }

            int parenthesisOffset = rule.IndexOf('(', candidateOffset);
            if (parenthesisOffset < 0)
            {
                return false;
            }

            candidateOffset = parenthesisOffset + 1;
        }

        return false;
    }

    [DoesNotReturn]
    private static void ThrowUnstagedGeodataConsumer(string description)
    {
        throw CreateUnstagedGeodataConsumerException(description);
    }

    private static MihomoServiceConfigurationTrustException
        CreateUnstagedGeodataConsumerException(string description)
    {
        return new MihomoServiceConfigurationTrustException(
            $"The {description} requires geodata assets that were not staged by the service.");
    }

    private static void ValidateRootControllerAuthority(
        IReadOnlyDictionary<string, YamlNode> rootItems)
    {
        foreach (string forbiddenKey in ForbiddenControllerKeys)
        {
            if (rootItems.ContainsKey(forbiddenKey))
            {
                throw new MihomoServiceConfigurationTrustException(
                    $"The staged mihomo configuration contains forbidden root key '{forbiddenKey}'.");
            }
        }

        if (!rootItems.TryGetValue("external-controller", out YamlNode? controllerNode)
            || controllerNode is not YamlScalarNode controller
            || !string.Equals(
                controller.Value,
                RequiredControllerEndpoint,
                StringComparison.Ordinal))
        {
            throw new MihomoServiceConfigurationTrustException(
                "The source controller must retain the Clash# loopback endpoint before overlay.");
        }

        if (!rootItems.TryGetValue("secret", out YamlNode? secretNode)
            || secretNode is not YamlScalarNode secret
            || !MihomoServiceIpcProtocol.IsCanonicalSha256(secret.Value))
        {
            throw new MihomoServiceConfigurationTrustException(
                "The source controller secret must retain its managed shape before overlay.");
        }
    }

    private static void ValidateManagedServiceAuthority(
        IReadOnlyDictionary<string, YamlNode> rootItems)
    {
        string mixedPortText = ReadRequiredScalar(
            rootItems,
            "mixed-port",
            "root mapping");
        if (!int.TryParse(
                mixedPortText,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out int mixedPort)
            || mixedPort is < 1 or > ushort.MaxValue)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The service source mixed-port is invalid.");
        }

        if (!ReadRequiredScalar(rootItems, "allow-lan", "root mapping")
                .Equals("false", StringComparison.Ordinal)
            || !ReadRequiredScalar(rootItems, "bind-address", "root mapping")
                .Equals("127.0.0.1", StringComparison.Ordinal))
        {
            throw new MihomoServiceConfigurationTrustException(
                "The service source LAN authority is not canonical.");
        }

        string mode = ReadRequiredScalar(rootItems, "mode", "root mapping");
        if (mode is not "rule" and not "global")
        {
            throw new MihomoServiceConfigurationTrustException(
                "The service source mode must be rule or global.");
        }

        if (!rootItems.TryGetValue("tun", out YamlNode? tunNode)
            || tunNode is not YamlMappingNode tun)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The service source requires the managed TUN mapping.");
        }

        Dictionary<string, YamlNode> fields = ReadUniqueMapping(tun, "'tun' section");
        if (!fields.Keys.ToHashSet(StringComparer.Ordinal).SetEquals(RequiredTunKeys)
            || ReadRequiredScalar(fields, "enable", "'tun' section") != "true"
            || ReadRequiredScalar(fields, "stack", "'tun' section") != "system"
            || ReadRequiredScalar(fields, "auto-route", "'tun' section") != "true"
            || ReadRequiredScalar(fields, "auto-detect-interface", "'tun' section") != "true"
            || ReadRequiredScalar(fields, "strict-route", "'tun' section") != "false"
            || fields["dns-hijack"] is not YamlSequenceNode dnsHijack
            || dnsHijack.Children.Count != 1
            || dnsHijack.Children[0] is not YamlScalarNode { Value: "any:53" })
        {
            throw new MihomoServiceConfigurationTrustException(
                "The service source TUN authority is not canonical.");
        }
    }

    private static void RejectDnsListener(IReadOnlyDictionary<string, YamlNode> rootItems)
    {
        if (!rootItems.TryGetValue("dns", out YamlNode? dnsNode))
        {
            return;
        }

        if (dnsNode is not YamlMappingNode dns)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The service DNS section must be a mapping.");
        }

        Dictionary<string, YamlNode> fields = ReadUniqueMapping(dns, "'dns' section");
        if (fields.ContainsKey("listen"))
        {
            throw new MihomoServiceConfigurationTrustException(
                "A service-owned DNS listener is not permitted.");
        }
    }

    internal static string ValidateProviderPath(string runtimeDirectory, string providerPath)
    {
        string normalizedRuntimeDirectory = ValidateRuntimeDirectory(runtimeDirectory);
        string[] components = ValidateProviderRelativePath(providerPath);

        string fullPath = Path.GetFullPath(
            Path.Combine(normalizedRuntimeDirectory, Path.Combine(components)));
        string relativePath = Path.GetRelativePath(normalizedRuntimeDirectory, fullPath);
        if (Path.IsPathFullyQualified(relativePath)
            || relativePath.Equals("..", StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || relativePath.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new MihomoServiceConfigurationTrustException(
                "Provider path escaped the protected runtime directory.");
        }

        ValidateExistingPathComponents(normalizedRuntimeDirectory, components);
        return fullPath;
    }

    internal static string[] ValidateProviderRelativePath(string providerPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerPath);
        if (providerPath.Length > 1024
            || Path.IsPathRooted(providerPath)
            || providerPath.AsSpan().IndexOfAny(WindowsInvalidFileNameCharacters) >= 0)
        {
            throw new MihomoServiceConfigurationTrustException(
                "Provider paths must be canonical relative filesystem paths.");
        }

        string[] components = providerPath.Split(
            ['/', '\\'],
            StringSplitOptions.None);
        if (components.Length == 0
            || components.Any(component => string.IsNullOrWhiteSpace(component)
                || component is "." or ".."
                || component.EndsWith(' ')
                || component.EndsWith('.')
                || IsReservedDeviceName(component)))
        {
            throw new MihomoServiceConfigurationTrustException(
                "Provider paths must be canonical relative filesystem paths.");
        }

        return components;
    }

    private static YamlMappingNode LoadUniqueRoot(string configurationText)
    {
        YamlStream stream = new();
        try
        {
            using StringReader reader = new(configurationText);
            stream.Load(reader);
        }
        catch (YamlException exception)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The staged mihomo configuration is not valid YAML.",
                exception);
        }

        if (stream.Documents.Count != 1
            || stream.Documents[0].RootNode is not YamlMappingNode root)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The staged mihomo configuration must contain one root mapping.");
        }

        return root;
    }

    private static Dictionary<string, YamlNode> ReadUniqueMapping(
        YamlMappingNode mapping,
        string description)
    {
        Dictionary<string, YamlNode> items = new(StringComparer.Ordinal);
        foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode key || string.IsNullOrEmpty(key.Value))
            {
                throw new MihomoServiceConfigurationTrustException(
                    $"The {description} contains a non-scalar key.");
            }

            if (!items.TryAdd(key.Value, valueNode))
            {
                throw new MihomoServiceConfigurationTrustException(
                    $"The {description} contains duplicate key '{key.Value}'.");
            }
        }

        return items;
    }

    private static string ReadRequiredScalar(
        IReadOnlyDictionary<string, YamlNode> mapping,
        string key,
        string description)
    {
        if (!mapping.TryGetValue(key, out YamlNode? node)
            || node is not YamlScalarNode scalar
            || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new MihomoServiceConfigurationTrustException(
                $"The {description} requires scalar '{key}'.");
        }

        return scalar.Value;
    }

    private static string ReadScalar(YamlNode node, string description)
    {
        if (node is not YamlScalarNode scalar || string.IsNullOrWhiteSpace(scalar.Value))
        {
            throw new MihomoServiceConfigurationTrustException(
                $"The {description} must be a nonempty scalar.");
        }

        return scalar.Value;
    }

    private static void RejectKnownLocalSystemSurfaces(YamlNode node)
    {
        HashSet<YamlNode> visited = new(ReferenceEqualityComparer.Instance);
        Stack<YamlNode> pending = new();
        pending.Push(node);
        while (pending.TryPop(out YamlNode? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            switch (current)
            {
                case YamlMappingNode mapping:
                    bool isSshMapping = mapping.Children.Any(pair =>
                        pair.Key is YamlScalarNode { Value: "type" }
                        && pair.Value is YamlScalarNode type
                        && string.Equals(type.Value, "ssh", StringComparison.OrdinalIgnoreCase));
                    foreach ((YamlNode keyNode, YamlNode valueNode) in mapping.Children)
                    {
                        if (keyNode is YamlScalarNode { Value: string key })
                        {
                            if (ForbiddenLocalCredentialKeys.Contains(key))
                            {
                                throw new MihomoServiceConfigurationTrustException(
                                    $"The staged mihomo configuration contains forbidden key '{key}'.");
                            }

                            if (isSshMapping
                                && key.Equals("private-key", StringComparison.Ordinal))
                            {
                                throw new MihomoServiceConfigurationTrustException(
                                    "SSH private-key filesystem access is not permitted for LocalSystem.");
                            }

                            if (key.Equals("state-dir", StringComparison.Ordinal))
                            {
                                throw new MihomoServiceConfigurationTrustException(
                                    "User-selected state directories are not permitted for LocalSystem.");
                            }

                            if (key.Equals("file-descriptor", StringComparison.Ordinal))
                            {
                                throw new MihomoServiceConfigurationTrustException(
                                    "TUN file-descriptor injection is not permitted for LocalSystem.");
                            }

                            if (key.Equals("geo-auto-update", StringComparison.Ordinal)
                                && !IsExplicitFalseScalar(valueNode))
                            {
                                throw new MihomoServiceConfigurationTrustException(
                                    "Automatic geodata writes are not permitted for LocalSystem.");
                            }

                            if (key.Equals("write-to-system", StringComparison.Ordinal)
                                && !IsExplicitFalseScalar(valueNode))
                            {
                                throw new MihomoServiceConfigurationTrustException(
                                    "NTP system clock writes are not permitted for LocalSystem.");
                            }

                        }

                        pending.Push(keyNode);
                        pending.Push(valueNode);
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                    {
                        pending.Push(child);
                    }

                    break;
            }
        }
    }

    private static bool IsExplicitFalseScalar(YamlNode node)
    {
        if (node is not YamlScalarNode scalar || scalar.Value is null)
        {
            return false;
        }

        return scalar.Value.Trim() switch
        {
            "0" => true,
            string value when value.Equals("false", StringComparison.OrdinalIgnoreCase)
                || value.Equals("no", StringComparison.OrdinalIgnoreCase)
                || value.Equals("off", StringComparison.OrdinalIgnoreCase) => true,
            _ => false,
        };
    }

    private static void RejectMergeKeys(YamlNode node)
    {
        HashSet<YamlNode> visited = new(ReferenceEqualityComparer.Instance);
        Stack<YamlNode> pending = new();
        pending.Push(node);
        while (pending.TryPop(out YamlNode? current))
        {
            if (!visited.Add(current))
            {
                continue;
            }

            if (visited.Count > MaximumYamlNodeCount)
            {
                throw new MihomoServiceConfigurationTrustException(
                    "The staged mihomo configuration is too structurally complex.");
            }

            switch (current)
            {
                case YamlMappingNode mapping:
                    foreach ((YamlNode key, YamlNode value) in mapping.Children)
                    {
                        if (key is YamlScalarNode { Value: "<<" })
                        {
                            throw new MihomoServiceConfigurationTrustException(
                                "YAML merge keys are not permitted in LocalSystem configuration.");
                        }

                        pending.Push(key);
                        pending.Push(value);
                    }

                    break;
                case YamlSequenceNode sequence:
                    foreach (YamlNode child in sequence.Children)
                    {
                        pending.Push(child);
                    }

                    break;
            }
        }
    }

    private static string ValidateRuntimeDirectory(string runtimeDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeDirectory);
        string fullPath = Path.GetFullPath(runtimeDirectory);
        FileAttributes attributes;
        try
        {
            attributes = File.GetAttributes(fullPath);
        }
        catch (Exception exception) when (exception is FileNotFoundException
            or DirectoryNotFoundException)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The protected runtime directory does not exist.",
                exception);
        }

        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new MihomoServiceConfigurationTrustException(
                "The protected runtime directory is not a regular directory.");
        }

        return Path.TrimEndingDirectorySeparator(fullPath);
    }

    private static void ValidateExistingPathComponents(
        string runtimeDirectory,
        IReadOnlyList<string> components)
    {
        string current = runtimeDirectory;
        for (int index = 0; index < components.Count; index++)
        {
            current = Path.Combine(current, components[index]);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(current);
            }
            catch (Exception exception) when (exception is FileNotFoundException
                or DirectoryNotFoundException)
            {
                return;
            }

            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                throw new MihomoServiceConfigurationTrustException(
                    "Provider path contains a reparse point.");
            }

            if (index < components.Count - 1
                && (attributes & FileAttributes.Directory) == 0)
            {
                throw new MihomoServiceConfigurationTrustException(
                    "Provider path has a non-directory parent component.");
            }
        }
    }

    private static bool IsReservedDeviceName(string component)
    {
        string name = component.Split('.', 2)[0];
        if (name.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || name.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || name.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || name.Equals("NUL", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return name.Length == 4
            && (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && name[3] is >= '1' and <= '9';
    }
}
