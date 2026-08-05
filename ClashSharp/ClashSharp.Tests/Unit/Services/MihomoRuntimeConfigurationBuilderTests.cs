using System;
using System.IO;
using ClashSharp.Model;
using ClashSharp.Service;
using YamlDotNet.RepresentationModel;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests pure mihomo runtime configuration generation behavior.</summary>
/// <remarks>
/// Invariants: Tests must not start mihomo, mutate Windows proxy settings, or touch user application data.
/// Thread safety: xUnit may run tests concurrently; tested methods are stateless.
/// Side effects: None.
/// </remarks>
public sealed class MihomoRuntimeConfigurationBuilderTests
{
    private const string ControllerSecret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static readonly string[] AdditionalAppOwnedRuntimeKeys =
    [
        "ss-config",
        "vmess-config",
        "tuic-server",
        "tunnels",
        "external-controller-routing-mark",
        "external-doh-server",
        "external-ui",
        "external-ui-name",
        "external-ui-url",
        "inbound-tfo",
        "inbound-mptcp",
    ];

    /// <summary>Returns newly covered RawConfig keys for generated-state validation tests.</summary>
    public static IEnumerable<object[]> AdditionalAppOwnedRuntimeKeyCases()
    {
        return AdditionalAppOwnedRuntimeKeys.Select(key => new object[] { key });
    }

    /// <summary>Verifies default configuration includes direct routing and the requested mixed port.</summary>
    [Fact]
    public void BuildDefaultConfiguration_StandbyWithoutTun_EmitsDirectModeAndPort()
    {
        string configuration = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
            7890,
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.Contains("mixed-port: 7890", configuration, StringComparison.Ordinal);
        Assert.Contains("mode: direct", configuration, StringComparison.Ordinal);
        Assert.Contains("rules:\n  - MATCH,DIRECT", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("tun:\n", configuration, StringComparison.Ordinal);
        Assert.Contains("external-controller: 127.0.0.1:9090", configuration, StringComparison.Ordinal);
        Assert.Contains($"secret: '{ControllerSecret}'", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies default configuration includes Clash# controlled TUN settings when requested.</summary>
    [Fact]
    public void BuildDefaultConfiguration_RuleTakeoverWithTun_EmitsTunSection()
    {
        string configuration = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
            7891,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            ControllerSecret);

        Assert.Contains("mixed-port: 7891", configuration, StringComparison.Ordinal);
        Assert.Contains("mode: rule", configuration, StringComparison.Ordinal);
        Assert.True(MihomoYamlSemanticValidator.ReadActivationPlan(configuration, "test").TunEnabled);
        Assert.Contains("  auto-route: true", configuration, StringComparison.Ordinal);
        Assert.Contains("dns:\n  enable: true", configuration, StringComparison.Ordinal);
        Assert.Contains("  enhanced-mode: fake-ip", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies imported runtime keys are replaced while unrelated profile content remains.</summary>
    [Fact]
    public void OverrideRuntimeKeys_ExistingKeys_ReplacesControlledValues()
    {
        const string ImportedConfiguration = """
            mixed-port: 1080
            mode: direct
            tun:
              enable: false
              stack: gvisor
            proxies:
              - name: US Node
                type: ss
                server: example.invalid
                port: 443
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - US Node
            rules:
              - MATCH,GLOBAL
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7892,
            ClashSharpMode.FullTakeover,
            transparentProxyEnabled: true,
            ControllerSecret);

        Assert.Contains("mixed-port: 7892", configuration, StringComparison.Ordinal);
        Assert.Contains("mode: global", configuration, StringComparison.Ordinal);
        Assert.True(MihomoYamlSemanticValidator.ReadActivationPlan(configuration, "test").TunEnabled);
        Assert.DoesNotContain("stack: gvisor", configuration, StringComparison.Ordinal);
        Assert.Contains("US Node", configuration, StringComparison.Ordinal);
        Assert.Contains("- MATCH,GLOBAL", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies missing controlled keys are inserted deterministically.</summary>
    [Fact]
    public void OverrideRuntimeKeys_MissingKeys_InsertsModeAndPort()
    {
        const string ImportedConfiguration = """
            proxies: []
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            rules:
              - MATCH,DIRECT
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7893,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.StartsWith(
            $"external-controller: 127.0.0.1:9090\nsecret: '{ControllerSecret}'\nallow-lan: false\nbind-address: 127.0.0.1\nmode: rule\nmixed-port: 7893\n",
            configuration,
            StringComparison.Ordinal);
        Assert.DoesNotContain("tun:\n", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies imported profiles cannot redirect or weaken the app-owned controller.</summary>
    [Fact]
    public void OverrideRuntimeKeys_ImportedControllerSettings_ReplacesWithAuthenticatedLoopbackEndpoint()
    {
        const string ImportedConfiguration = """
            "external-controller" : 0.0.0.0:9091
            external-controller-tls: 0.0.0.0:9443
            external-controller-pipe: \\.\pipe\untrusted
            external-controller-unix: /tmp/untrusted.sock
            external-controller-cors:
              allow-origins:
                - '*'
            'secret': subscription-secret
            allow-lan : true
            bind-address: '*'
            port: 8080
            socks-port: 1080
            listeners:
              - name: exposed
                type: socks
                port: 1081
                listen: 0.0.0.0
            proxies: []
            rules:
              - MATCH,DIRECT
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7894,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.Equal(1, CountLines(configuration, "external-controller: 127.0.0.1:9090"));
        Assert.Equal(1, CountLines(configuration, $"secret: '{ControllerSecret}'"));
        Assert.DoesNotContain("0.0.0.0", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("external-controller-tls:", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("external-controller-pipe:", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("external-controller-unix:", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("external-controller-cors:", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription-secret", configuration, StringComparison.Ordinal);
        Assert.Equal(1, CountLines(configuration, "allow-lan: false"));
        Assert.Equal(1, CountLines(configuration, "bind-address: 127.0.0.1"));
        Assert.DoesNotContain("port: 8080", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("socks-port:", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("listeners:", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies hostile profiles cannot retain legacy listeners, tunnels, or UI/file controls.</summary>
    [Fact]
    public void OverrideRuntimeKeys_HostileInboundAndControlSettings_RemovesEveryOwnedRootKey()
    {
        const string ImportedConfiguration = """
            ss-config: 'ss://aes-128-gcm:password@0.0.0.0:8388'
            vmess-config: 'vmess://hostile:5f1d14bb-21fd-4f99-b395-14b9dd3ab832@0.0.0.0:8389'
            tuic-server:
              enable: true
              listen: 0.0.0.0:443
              certificate: 'C:\hostile\server.crt'
              private-key: 'C:\hostile\server.key'
            tunnels:
              - network: tcp
                address: 0.0.0.0:7000
                target: hostile.invalid:7001
            external-controller-routing-mark: 666
            external-doh-server: https://hostile.invalid/dns-query
            external-ui: 'C:\Windows\System32'
            external-ui-name: hostile-dashboard
            external-ui-url: https://hostile.invalid/dashboard.zip
            inbound-tfo: true
            inbound-mptcp: true
            log-level: debug
            proxies: []
            rules:
              - MATCH,DIRECT
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7894,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        foreach (string key in AdditionalAppOwnedRuntimeKeys)
        {
            Assert.False(HasRootKey(configuration, key), $"Retained hostile root key '{key}'.");
        }

        Assert.DoesNotContain("hostile.invalid", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("C:\\hostile", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("log-level: debug", configuration, StringComparison.Ordinal);
        Assert.Equal(1, CountLines(configuration, "log-level: info"));
        Assert.True(HasRootKey(configuration, "proxies"));
        Assert.True(HasRootKey(configuration, "rules"));
    }

    /// <summary>Verifies generated-state validation rejects every newly app-owned RawConfig key.</summary>
    [Theory]
    [MemberData(nameof(AdditionalAppOwnedRuntimeKeyCases))]
    public void ValidateManagedRuntimeConfiguration_AdditionalOwnedRootKey_Throws(string key)
    {
        string configuration = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
            7894,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);
        string hostileConfiguration = configuration + $"{key}: hostile\n";

        ArgumentException exception = Assert.Throws<ArgumentException>(() =>
            MihomoYamlSemanticValidator.ValidateManagedRuntimeConfiguration(
                hostileConfiguration,
                7894,
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                ControllerSecret));

        Assert.Contains(key, exception.Message, StringComparison.Ordinal);
    }

    /// <summary>Verifies a profile cannot enable mihomo's unauthenticated debug controller routes.</summary>
    [Fact]
    public void ValidateManagedRuntimeConfiguration_DebugLogLevel_Throws()
    {
        string configuration = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
            7894,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);
        string hostileConfiguration = configuration.Replace(
            "log-level: info",
            "log-level: debug",
            StringComparison.Ordinal);

        Assert.Throws<ArgumentException>(() =>
            MihomoYamlSemanticValidator.ValidateManagedRuntimeConfiguration(
                hostileConfiguration,
                7894,
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                ControllerSecret));
    }

    /// <summary>Verifies a conventional YAML document marker does not split managed keys from profile content.</summary>
    [Fact]
    public void OverrideRuntimeKeys_SingleDocumentMarkers_PreservesProfileContentInManagedDocument()
    {
        const string ImportedConfiguration = """
            # subscription profile
            ---
            proxies:
              - name: Node A
                type: direct
            rules:
              - MATCH,Node A
            ...
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.Contains("name: Node A", configuration, StringComparison.Ordinal);
        Assert.True(HasRootKey(configuration, "proxies"));
    }

    /// <summary>Verifies comments on conventional YAML boundaries remain a single managed document.</summary>
    [Fact]
    public void OverrideRuntimeKeys_CommentedDocumentMarkers_PreservesProfileContentInManagedDocument()
    {
        const string ImportedConfiguration = """
            --- # subscription document
            proxies:
              - name: Node B
                type: direct
            rules:
              - MATCH,Node B
            ... # end subscription document
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.Contains("name: Node B", configuration, StringComparison.Ordinal);
        Assert.True(HasRootKey(configuration, "rules"));
    }

    /// <summary>Verifies comments and blank lines inside an owned mapping cannot leave orphaned children.</summary>
    [Fact]
    public void OverrideRuntimeKeys_OwnedMappingWithTrivia_RemovesAllChildren()
    {
        const string ImportedConfiguration = """
            listeners:
              - name: first-untrusted-listener
                type: socks

            # A YAML comment does not end the listeners mapping.
              - name: second-untrusted-listener
                type: http
            proxies: []
            rules:
              - MATCH,DIRECT
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.DoesNotContain("untrusted-listener", configuration, StringComparison.Ordinal);
        Assert.True(HasRootKey(configuration, "proxies"));
        Assert.Contains("rules:", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies valid indentationless sequence values are removed with their owned key.</summary>
    [Fact]
    public void OverrideRuntimeKeys_OwnedIndentationlessSequence_RemovesAllItems()
    {
        const string ImportedConfiguration = """
            authentication:
            - untrusted-user:untrusted-password
            proxies: []
            rules: []
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.DoesNotContain("untrusted-user", configuration, StringComparison.Ordinal);
        Assert.True(HasRootKey(configuration, "proxies"));
    }

    /// <summary>Verifies anchors and tags on owned indentationless sequences do not leave orphaned items.</summary>
    [Theory]
    [InlineData("authentication: &auth\n- untrusted-user:untrusted-password\nproxies: []\nrules: []\n")]
    [InlineData("authentication: !!seq\n- untrusted-user:untrusted-password\nproxies: []\nrules: []\n")]
    [InlineData("authentication: !!seq &auth # credentials\n- untrusted-user:untrusted-password\nproxies: []\nrules: []\n")]
    public void OverrideRuntimeKeys_OwnedNodePropertiesWithIndentationlessSequence_RemovesAllItems(
        string importedConfiguration)
    {
        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            importedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.DoesNotContain("untrusted-user", configuration, StringComparison.Ordinal);
        Assert.True(HasRootKey(configuration, "proxies"));
    }

    /// <summary>Verifies AST serialization reanchors a retained value that referenced an owned field.</summary>
    [Fact]
    public void OverrideRuntimeKeys_RetainedAliasReferencesRemovedOwnedAnchor_ReanchorsRetainedValue()
    {
        const string ImportedConfiguration = """
            authentication: &shared
            - example.com
            sniffer:
              enable: true
              skip-domain: *shared
            proxies: []
            rules: []
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.False(HasRootKey(configuration, "authentication"));
        Assert.True(HasRootKey(configuration, "sniffer"));
        Assert.Contains("example.com", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies an alias used as a retained mapping key is matched without its YAML separator.</summary>
    [Fact]
    public void OverrideRuntimeKeys_RetainedAliasMappingKeyReferencesRemovedOwnedAnchor_Throws()
    {
        const string ImportedConfiguration = """
            secret: &hostkey example.com
            hosts:
              *hostkey: 127.0.0.1
            proxies: []
            rules: []
            """;

        Assert.Throws<ArgumentException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
                ImportedConfiguration,
                7895,
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                ControllerSecret));
    }

    /// <summary>Verifies escaped quoted keys are compared by decoded YAML scalar value.</summary>
    [Fact]
    public void OverrideRuntimeKeys_EscapedOwnedKey_ReplacesDecodedControllerKey()
    {
        const string ImportedConfiguration = """
            "external\u002dcontroller": 0.0.0.0:9091
            "secr\x65t": subscription-secret
            proxies: []
            rules: []
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.Equal(1, CountLines(configuration, "external-controller: 127.0.0.1:9090"));
        Assert.Equal(1, CountLines(configuration, $"secret: '{ControllerSecret}'"));
        Assert.DoesNotContain("0.0.0.0", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("subscription-secret", configuration, StringComparison.Ordinal);
    }

    /// <summary>Verifies AST overlay safely handles valid complex serialization forms.</summary>
    [Theory]
    [InlineData("? external-controller\n: 0.0.0.0:9091\nproxies: []\n")]
    [InlineData("!!str external-controller: 0.0.0.0:9091\nproxies: []\n")]
    [InlineData("{ external-controller: 0.0.0.0:9091, proxies: [] }\n")]
    public void OverrideRuntimeKeys_ValidYamlKeyForms_ReplacesOwnedController(string importedConfiguration)
    {
        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            importedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: false,
            ControllerSecret);

        Assert.Contains("external-controller: 127.0.0.1:9090", configuration, StringComparison.Ordinal);
        Assert.DoesNotContain("0.0.0.0", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void OverrideRuntimeKeys_TunWithoutDns_AddsManagedSafeDnsDefaults()
    {
        const string ImportedConfiguration = "proxies: []\nrules: []\n";

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            ControllerSecret);

        Assert.Contains("dns:", configuration, StringComparison.Ordinal);
        Assert.Contains("enhanced-mode: fake-ip", configuration, StringComparison.Ordinal);
        Assert.Contains("default-nameserver:", configuration, StringComparison.Ordinal);
        Assert.Contains("nameserver:", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void OverrideRuntimeKeys_ExistingDns_PreservesValuesAndFillsRequiredMissingFields()
    {
        const string ImportedConfiguration = """
            dns:
              enable: true
              nameserver:
                - https://dns.example/dns-query
              fallback:
                - tls://dns.example
            proxies: []
            rules: []
            """;

        string configuration = MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
            ImportedConfiguration,
            7895,
            ClashSharpMode.RuleTakeover,
            transparentProxyEnabled: true,
            ControllerSecret);

        Assert.Contains("https://dns.example/dns-query", configuration, StringComparison.Ordinal);
        Assert.Contains("tls://dns.example", configuration, StringComparison.Ordinal);
        Assert.Contains("enhanced-mode: fake-ip", configuration, StringComparison.Ordinal);
        Assert.Contains("default-nameserver:", configuration, StringComparison.Ordinal);
    }

    [Fact]
    public void OverrideRuntimeKeys_TunWithExplicitlyDisabledDns_Throws()
    {
        const string ImportedConfiguration = """
            dns:
              enable: false
              enhanced-mode: fake-ip
              default-nameserver: [1.1.1.1]
              nameserver: [https://1.1.1.1/dns-query]
            proxies: []
            rules: []
            """;

        Assert.Throws<ArgumentException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
                ImportedConfiguration,
                7895,
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: true,
                ControllerSecret));
    }

    [Theory]
    [InlineData("- proxies\n- rules\n")]
    [InlineData("mixed-port: 1\n\"mixed-port\": 2\nproxies: []\n")]
    public void OverrideRuntimeKeys_AmbiguousRootShape_Throws(string importedConfiguration)
    {
        Assert.Throws<ArgumentException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
                importedConfiguration,
                7895,
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                ControllerSecret));
    }

    /// <summary>Verifies root merge keys cannot reintroduce app-owned controller or listener fields.</summary>
    [Theory]
    [InlineData("defaults: &defaults\n  authentication:\n    - user:password\n<<: *defaults\nproxies: []\n")]
    [InlineData("defaults: &defaults\n  listeners: []\n\"<<\": *defaults\nproxies: []\n")]
    public void OverrideRuntimeKeys_TopLevelYamlMerge_Throws(string importedConfiguration)
    {
        Assert.Throws<ArgumentException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
                importedConfiguration,
                7895,
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                ControllerSecret));
    }

    /// <summary>Verifies multi-document YAML is rejected instead of silently ignoring the selected profile.</summary>
    [Fact]
    public void OverrideRuntimeKeys_MultipleYamlDocuments_Throws()
    {
        const string ImportedConfiguration = "proxies: []\n---\nrules: []\n";

        Assert.Throws<ArgumentException>(() =>
            MihomoRuntimeConfigurationBuilder.OverrideRuntimeKeys(
                ImportedConfiguration,
                7896,
                ClashSharpMode.RuleTakeover,
                transparentProxyEnabled: false,
                ControllerSecret));
    }

    /// <summary>Verifies TUN enablement follows the selected master status and user preference.</summary>
    [Theory]
    [InlineData(ClashSharpMode.Disabled, true, false)]
    [InlineData(ClashSharpMode.Standby, true, false)]
    [InlineData(ClashSharpMode.RuleTakeover, true, true)]
    [InlineData(ClashSharpMode.FullTakeover, true, true)]
    [InlineData(ClashSharpMode.RuleTakeover, false, false)]
    public void ShouldEnableTransparentProxy_UsesActiveTakeoverModesOnly(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        bool expected)
    {
        bool actual = MihomoRuntimeConfigurationBuilder.ShouldEnableTransparentProxy(mode, transparentProxyEnabled);

        Assert.Equal(expected, actual);
    }

    /// <summary>Verifies invalid ports are rejected before configuration text is emitted.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void BuildDefaultConfiguration_InvalidPort_Throws(int mixedPort)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
                mixedPort,
                ClashSharpMode.Standby,
                transparentProxyEnabled: false,
                ControllerSecret));
    }

    /// <summary>Verifies unsafe or malformed controller secrets cannot be emitted as YAML scalars.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("not-a-256-bit-secret")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF")]
    public void BuildDefaultConfiguration_InvalidControllerSecret_Throws(string controllerSecret)
    {
        Assert.Throws<ArgumentException>(() =>
            MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
                7890,
                ClashSharpMode.Standby,
                transparentProxyEnabled: false,
                controllerSecret));
    }

    /// <summary>Verifies even an all-digit valid secret is emitted as an explicit YAML string.</summary>
    [Fact]
    public void BuildDefaultConfiguration_AllDigitControllerSecret_QuotesScalar()
    {
        const string NumericSecret = "0000000000000000000000000000000000000000000000000000000000000000";

        string configuration = MihomoRuntimeConfigurationBuilder.BuildDefaultConfiguration(
            7890,
            ClashSharpMode.Standby,
            transparentProxyEnabled: false,
            NumericSecret);

        Assert.Contains($"secret: '{NumericSecret}'", configuration, StringComparison.Ordinal);
    }

    private static int CountLines(string configuration, string expectedLine)
    {
        return configuration.Split('\n').Count(line => StringComparer.Ordinal.Equals(line, expectedLine));
    }

    private static bool HasRootKey(string configuration, string expectedKey)
    {
        YamlStream stream = new();
        using StringReader reader = new(configuration);
        stream.Load(reader);
        YamlMappingNode root = Assert.IsType<YamlMappingNode>(Assert.Single(stream.Documents).RootNode);
        return root.Children.Keys.Any(
            key => key is YamlScalarNode scalar
                && StringComparer.Ordinal.Equals(scalar.Value, expectedKey));
    }
}
