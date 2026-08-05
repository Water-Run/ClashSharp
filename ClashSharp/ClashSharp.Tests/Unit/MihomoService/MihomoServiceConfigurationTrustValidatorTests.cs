using ClashSharp.MihomoService;

namespace ClashSharp.Tests.Unit.MihomoService;

/// <summary>Regression tests for LocalSystem configuration filesystem escape attempts.</summary>
public sealed class MihomoServiceConfigurationTrustValidatorTests
{
    /// <summary>Verifies the LocalSystem child receives no inherited Mihomo override variables.</summary>
    [Fact]
    public void ChildEnvironment_IsMinimalAndUsesProtectedRuntimeForTemporaryFiles()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);

        IReadOnlyDictionary<string, string> environment =
            WindowsMihomoChildProcessLauncher.CreateSafeEnvironment(runtimeDirectory);

        Assert.Equal(
            ["LISTEN_NAMEDPIPE_SDDL", "PATH", "SystemRoot", "TEMP", "TMP", "WINDIR"],
            environment.Keys.Order(StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(
            environment.Keys,
            key => key.StartsWith("CLASH_", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SAFE_PATHS", StringComparison.OrdinalIgnoreCase)
                || key.Equals("SKIP_SAFE_PATH_CHECK", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(Environment.SystemDirectory, environment["PATH"]);
        Assert.Equal(
            "D:P(A;;GA;;;SY)",
            environment["LISTEN_NAMEDPIPE_SDDL"]);
        Assert.StartsWith(runtimeDirectory, environment["TEMP"], StringComparison.OrdinalIgnoreCase);
        Assert.Equal(environment["TEMP"], environment["TMP"]);
    }

    /// <summary>Verifies provider paths cannot select an absolute path outside the protected runtime.</summary>
    [Theory]
    [InlineData(@"C:\Windows\System32\drivers\etc\hosts")]
    [InlineData(@"\\server\share\provider.yaml")]
    [InlineData(@"\Windows\Temp\provider.yaml")]
    public void ValidateText_RootedProviderPath_IsRejected(string providerPath)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateProviderPath(
                runtimeDirectory,
                providerPath));
    }

    /// <summary>Verifies lexical traversal and Windows device aliases cannot escape the runtime.</summary>
    [Theory]
    [InlineData("../provider.yaml")]
    [InlineData("providers/../../provider.yaml")]
    [InlineData("./provider.yaml")]
    [InlineData("providers//provider.yaml")]
    [InlineData("NUL")]
    [InlineData("providers/COM1.yaml")]
    public void ValidateText_NonCanonicalProviderPath_IsRejected(string providerPath)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateProviderPath(
                runtimeDirectory,
                providerPath));
    }

    /// <summary>Verifies the known external UI download/write surface is disabled for LocalSystem.</summary>
    [Theory]
    [InlineData("external-ui")]
    [InlineData("external-ui-name")]
    [InlineData("external-ui-url")]
    public void ValidateText_ExternalUiKey_IsRejected(string key)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            $"mixed-port: 7890\n{key}: unsafe\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies user geodata URL overrides cannot trigger LocalSystem downloads.</summary>
    [Fact]
    public void ValidateText_GeoxUrlOverride_IsRejected()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "geox-url:\n"
            + "  geoip: https://attacker.invalid/geoip.dat\n"
            + "rules:\n"
            + "  - GEOIP,CN,DIRECT\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies geodata consumers pass source validation for later installer-asset staging.</summary>
    [Theory]
    [InlineData("rules:\n  - GEOIP,CN,DIRECT")]
    [InlineData("rules:\n  - IP-ASN,13335,DIRECT")]
    [InlineData("rules:\n  - AND,((DOMAIN,example.com),(GEOSITE,cn)),DIRECT")]
    [InlineData("sub-rules:\n  regional:\n    - SRC-IP-ASN,13335")]
    [InlineData("rule-providers:\n  geo:\n    type: inline\n    behavior: classical\n    payload:\n      - SRC-GEOIP,CN")]
    [InlineData("dns:\n  nameserver-policy:\n    'geosite:cn,private': 1.1.1.1")]
    [InlineData("dns:\n  nameserver-policy:\n    'example.com,geosite:cn': 1.1.1.1")]
    [InlineData("dns:\n  proxy-server-nameserver-policy:\n    'geosite:cn': 1.1.1.1")]
    [InlineData("dns:\n  fallback:\n    - 1.1.1.1")]
    [InlineData("dns:\n  fallback:\n    - 1.1.1.1\n  fallback-filter:\n    geoip: false\n    geosite:\n      - gfw")]
    [InlineData("dns:\n  enhanced-mode: fake-ip\n  fake-ip-filter:\n    - geosite:cn")]
    [InlineData("dns:\n  enhanced-mode: fake-ip\n  fake-ip-filter-mode: rule\n  fake-ip-filter:\n    - GEOSITE,cn,real-ip")]
    [InlineData("sniffer:\n  force-domain:\n    - geosite:cn")]
    [InlineData("sniffer:\n  skip-domain:\n    - geosite:private")]
    [InlineData("sniffer:\n  skip-src-address:\n    - geoip:cn")]
    [InlineData("sniffer:\n  skip-dst-address:\n    - geoip:private")]
    public void ValidateText_GeodataConsumer_IsAcceptedForAssetStaging(string geodataConsumer)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            $"mixed-port: 7890\n{geodataConsumer}\n");

        MihomoServiceConfigurationTrustValidator.ValidateText(configuration, runtimeDirectory);
    }

    /// <summary>Verifies unused defaults and ordinary non-geo expressions remain accepted.</summary>
    [Theory]
    [InlineData("dns:\n  nameserver:\n    - 1.1.1.1")]
    [InlineData("dns:\n  fallback-filter:\n    geoip: true")]
    [InlineData("dns:\n  fallback:\n    - 1.1.1.1\n  fallback-filter:\n    geoip: false")]
    [InlineData("rules:\n  - DOMAIN-SUFFIX,geoip.example,DIRECT")]
    public void ValidateText_ConfigurationWithoutActiveGeodataConsumer_IsAccepted(string safeContent)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            $"mixed-port: 7890\n{safeContent}\n");

        MihomoServiceConfigurationTrustValidator.ValidateText(configuration, runtimeDirectory);
    }

    /// <summary>Verifies additional known LocalSystem write/control surfaces are rejected.</summary>
    [Theory]
    [InlineData("external-controller-pipe: clash-control")]
    [InlineData("external-controller-unix: /tmp/clash.sock")]
    [InlineData("external-controller-tls: 127.0.0.1:9443")]
    [InlineData("geo-auto-update: true")]
    [InlineData("ntp:\n  enable: true\n  write-to-system: true")]
    [InlineData("tun:\n  enable: true\n  file-descriptor: 7")]
    [InlineData("certificate: C:/Users/attacker/cert.pem")]
    public void ValidateText_KnownLocalSystemSurface_IsRejected(string unsafeYaml)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);

        string configuration = unsafeYaml.StartsWith("tun:", StringComparison.Ordinal)
            ? MihomoServiceTestSupport
                .BuildManagedServiceConfiguration("mixed-port: 7890\n")
                .Replace(
                    "  dns-hijack:",
                    "  file-descriptor: 7\n  dns-hijack:",
                    StringComparison.Ordinal)
            : MihomoServiceTestSupport.BuildManagedServiceConfiguration(
                $"mixed-port: 7890\n{unsafeYaml}\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies controller authority rules apply only to root controller fields.</summary>
    [Fact]
    public void ValidateText_NestedProtocolSecret_IsAccepted()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "proxies:\n"
            + "  - name: nested-secret\n"
            + "    type: ss\n"
            + "    server: example.invalid\n"
            + "    port: 443\n"
            + "    cipher: aes-128-gcm\n"
            + "    secret: protocol-password\n");

        MihomoServiceConfigurationTrustValidator.ValidateText(configuration, runtimeDirectory);
    }

    /// <summary>Verifies untrusted root controller authority is rejected before overlay.</summary>
    [Theory]
    [InlineData("external-controller: 0.0.0.0:9090")]
    [InlineData("secret: not-a-managed-secret")]
    public void ValidateText_UnmanagedRootControllerAuthority_IsRejected(string authority)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);

        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            $"mixed-port: 7890\n{authority}\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies one exact TUN-only managed authority is accepted by the Service.</summary>
    [Fact]
    public void ValidateText_ExactManagedTunAuthority_IsAccepted()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\nproxy-providers:\n  local:\n    type: inline\n    payload: []\n");

        MihomoServiceConfigurationTrustValidator.ValidateText(
            configuration,
            runtimeDirectory);
    }

    /// <summary>Verifies LocalSystem cannot be used to open any additional inbound surface.</summary>
    [Theory]
    [InlineData("port: 8080")]
    [InlineData("socks-port: 1080")]
    [InlineData("redir-port: 7892")]
    [InlineData("tproxy-port: 7893")]
    [InlineData("listeners: []")]
    [InlineData("tuic-server:\n  enable: true")]
    [InlineData("ss-config: C:/Users/attacker/ss.yaml")]
    [InlineData("vmess-config: C:/Users/attacker/vmess.yaml")]
    [InlineData("tunnels: []")]
    [InlineData("authentication:\n  - user:password")]
    [InlineData("external-controller-routing-mark: 1")]
    [InlineData("external-doh-server: /dns-query")]
    [InlineData("inbound-tfo: true")]
    [InlineData("inbound-mptcp: true")]
    public void ValidateText_AdditionalInboundOrControlRoot_IsRejected(string hostileRoot)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            $"mixed-port: 7890\n{hostileRoot}\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies source LAN, mode, and exact TUN fields cannot be weakened.</summary>
    [Theory]
    [InlineData("allow-lan: false", "allow-lan: true")]
    [InlineData("bind-address: 127.0.0.1", "bind-address: 0.0.0.0")]
    [InlineData("mode: rule", "mode: direct")]
    [InlineData("  enable: true", "  enable: false")]
    [InlineData("  stack: system", "  stack: mixed")]
    [InlineData("  auto-route: true", "  auto-route: false")]
    [InlineData("  strict-route: false", "  strict-route: true")]
    [InlineData("    - any:53", "    - 0.0.0.0:53")]
    public void ValidateText_WeakenedManagedTunAuthority_IsRejected(
        string canonical,
        string hostile)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport
            .BuildManagedServiceConfiguration("mixed-port: 7890\n")
            .Replace(canonical, hostile, StringComparison.Ordinal);

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies SSH private key files cannot be opened through the LocalSystem child.</summary>
    [Fact]
    public void ValidateText_SshPrivateKey_IsRejected()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "proxies:\n"
            + "  - name: ssh\n"
            + "    type: ssh\n"
            + "    server: example.invalid\n"
            + "    private-key: C:/Users/attacker/id_ed25519\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies Tailscale cannot redirect LocalSystem state reads or writes.</summary>
    [Fact]
    public void ValidateText_TailscaleStateDirectory_IsRejected()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "proxies:\n"
            + "  - name: tailscale\n"
            + "    type: tailscale\n"
            + "    state-dir: C:/Users/attacker/tailscale\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies canonical file-provider sources pass validation for service-side copying.</summary>
    [Fact]
    public void ValidateText_FileProvider_IsAcceptedForAssetStaging()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "rule-providers:\n"
            + "  local:\n"
            + "    type: file\n"
            + "    path: rules/local.yaml\n");

        MihomoServiceConfigurationTrustValidator.ValidateText(configuration, runtimeDirectory);
    }

    /// <summary>Verifies native HTTP providers do not require a Clash#-specific size-limit key.</summary>
    [Theory]
    [InlineData("")]
    [InlineData("    size-limit: 0\n")]
    [InlineData("    size-limit: 16777217\n")]
    public void ValidateText_HttpProviderWithOptionalSizeLimit_IsAccepted(string sizeLimitLine)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "proxy-providers:\n"
            + "  source:\n"
            + "    type: http\n"
            + "    url: https://example.invalid/provider.yaml\n"
            + "    path: providers/source.yaml\n"
            + sizeLimitLine);

        MihomoServiceConfigurationTrustValidator.ValidateText(configuration, runtimeDirectory);
    }

    /// <summary>Verifies proxy and rule HTTP providers use the mature-client native download model.</summary>
    [Fact]
    public void ValidateText_HttpProviders_AreAccepted()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "proxy-providers:\n"
            + "  proxy-source:\n"
            + "    type: http\n"
            + "    url: https://example.invalid/proxies.yaml\n"
            + "    path: providers/proxy-source.yaml\n"
            + "    size-limit: 1048576\n"
            + "rule-providers:\n"
            + "  rules:\n"
            + "    type: http\n"
            + "    url: https://example.invalid/rules.yaml\n"
            + "    path: rules/rules.yaml\n"
            + "    size-limit: 1048576\n");

        MihomoServiceConfigurationTrustValidator.ValidateText(configuration, runtimeDirectory);
    }

    /// <summary>Verifies native provider downloads cannot be redirected to local files or credentials.</summary>
    [Theory]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("https://user:password@example.invalid/provider.yaml")]
    [InlineData("ftp://example.invalid/provider.yaml")]
    public void ValidateText_HttpProviderUnsafeUrl_IsRejected(string url)
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string configuration = MihomoServiceTestSupport.BuildManagedServiceConfiguration(
            "mixed-port: 7890\n"
            + "proxy-providers:\n"
            + "  source:\n"
            + "    type: http\n"
            + $"    url: '{url}'\n");

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateText(
                configuration,
                runtimeDirectory));
    }

    /// <summary>Verifies a nested provider junction is rejected even when the lexical path is local.</summary>
    [Fact]
    public void ValidateText_NestedProviderJunction_IsRejected()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        string runtimeDirectory = CreateRuntimeDirectory(temporaryDirectory.Path);
        string targetDirectory = Path.Combine(temporaryDirectory.Path, "target");
        Directory.CreateDirectory(targetDirectory);
        _ = Directory.CreateSymbolicLink(
            Path.Combine(runtimeDirectory, "providers"),
            targetDirectory);

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            MihomoServiceConfigurationTrustValidator.ValidateProviderPath(
                runtimeDirectory,
                "providers/provider.yaml"));
    }

    /// <summary>Verifies a pre-positioned runtime junction is rejected before any generation starts.</summary>
    [Fact]
    public void PrepareRuntimeDirectory_PreexistingRuntimeJunction_IsRejected()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        Directory.CreateDirectory(options.ServiceDataDirectory);
        string targetDirectory = Path.Combine(temporaryDirectory.Path, "target");
        Directory.CreateDirectory(targetDirectory);
        _ = Directory.CreateSymbolicLink(options.RuntimeDirectory, targetDirectory);
        MihomoGenerationStore store = new(options, protectDirectory: false);

        Assert.Throws<IOException>(() => store.PrepareRuntimeDirectory());
    }

    /// <summary>Verifies a nested pre-positioned runtime junction is rejected before launch.</summary>
    [Fact]
    public void PrepareRuntimeDirectory_PreexistingNestedJunction_IsRejected()
    {
        using MihomoServiceTemporaryDirectory temporaryDirectory = new();
        MihomoServiceOptions options = MihomoServiceTestSupport.CreateOptions(temporaryDirectory.Path);
        Directory.CreateDirectory(options.RuntimeDirectory);
        string targetDirectory = Path.Combine(temporaryDirectory.Path, "target");
        Directory.CreateDirectory(targetDirectory);
        _ = Directory.CreateSymbolicLink(
            Path.Combine(options.RuntimeDirectory, "providers"),
            targetDirectory);
        MihomoGenerationStore store = new(options, protectDirectory: false);

        Assert.Throws<IOException>(() => store.PrepareRuntimeDirectory());
    }

    /// <summary>Verifies an unsafe exact generation is rejected before a child process is created.</summary>
    [Fact]
    public async Task Start_RootedProviderPath_IsRejectedBeforeLaunch()
    {
        FakeMihomoChildProcess unusedProcess = new("unused", 909);
        await using MihomoChildSupervisorTestContext context = new([unusedProcess]);
        string hash = context.WriteConfiguration(
            BuildProviderConfiguration(@"C:\Windows\Temp\provider.yaml"));

        MihomoChildOperationResult result = await context.Supervisor.StartAsync(
            1,
            hash,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("service.child.configuration_untrusted", result.ErrorCode);
        Assert.Empty(context.Launcher.Requests);
    }

    /// <summary>Verifies missing installer geodata reaches the App as a stable repair diagnostic.</summary>
    [Fact]
    public async Task Start_MissingInstallerGeodata_ReturnsStableAssetCodeBeforeLaunch()
    {
        FakeMihomoChildProcess unusedProcess = new("unused", 910);
        await using MihomoChildSupervisorTestContext context = new([unusedProcess]);
        string hash = context.WriteConfiguration(
            "mixed-port: 7890\nrules:\n  - GEOIP,CN,DIRECT\n");

        MihomoChildOperationResult result = await context.Supervisor.StartAsync(
            2,
            hash,
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal("geo.assets_missing", result.ErrorCode);
        Assert.Empty(context.Launcher.Requests);
    }

    private static string BuildProviderConfiguration(string path)
    {
        return "mixed-port: 7890\n"
            + "proxy-providers:\n"
            + "  source:\n"
            + "    type: http\n"
            + "    url: https://example.invalid/provider.yaml\n"
            + $"    path: '{path.Replace("'", "''", StringComparison.Ordinal)}'\n"
            + "    size-limit: 1048576\n";
    }

    private static string CreateRuntimeDirectory(string root)
    {
        string runtimeDirectory = Path.Combine(root, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        return runtimeDirectory;
    }
}
