using System.Reflection;
using System.Text;
using ClashSharp.MihomoService;
using ClashSharp.Tests.Unit.MihomoService;

namespace ClashSharp.Tests.Unit.ServiceAndUi;

/// <summary>
/// Contract tests for the LocalSystem configuration trust validator. The private policy sets are read by
/// reflection, so adding a key to a set (or adding a new set) is covered automatically; a new set
/// that this class does not know how to classify fails the build of the test run on purpose.
/// </summary>
public sealed class TrustValidatorPolicyContractTests
{
    private const string BaseMixedPort = "mixed-port: 7890\n";

    private static readonly Dictionary<string, PolicyKind> PolicySetKinds = new(StringComparer.Ordinal)
    {
        ["ForbiddenExternalUiKeys"] = PolicyKind.ForbiddenAtRoot,
        ["ForbiddenServiceRootKeys"] = PolicyKind.ForbiddenAtRoot,
        ["ForbiddenControllerKeys"] = PolicyKind.ForbiddenAtRoot,
        ["ForbiddenLocalCredentialKeys"] = PolicyKind.ForbiddenAtAnyDepth,
        ["RequiredTunKeys"] = PolicyKind.ExactTunKeySet,
        ["ProviderSectionKeys"] = PolicyKind.ProviderSections,
    };

    private enum PolicyKind
    {
        ForbiddenAtRoot,
        ForbiddenAtAnyDepth,
        ExactTunKeySet,
        ProviderSections,
    }

    /// <summary>Gets one row per (key, value shape) of every root-level forbidden set.</summary>
    public static TheoryData<string, string> RootForbiddenKeyAndValue
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (string key in KeysOf(PolicyKind.ForbiddenAtRoot))
            {
                foreach (string shapeName in RootValueShapes.Keys)
                {
                    data.Add(key, shapeName);
                }
            }

            return data;
        }
    }

    /// <summary>Gets one row per (key, placement) of the any-depth credential set.</summary>
    public static TheoryData<string, string> CredentialKeyAndPlacement
    {
        get
        {
            TheoryData<string, string> data = [];
            foreach (string key in KeysOf(PolicyKind.ForbiddenAtAnyDepth))
            {
                foreach (string placement in Placements(string.Empty).Keys)
                {
                    data.Add(key, placement);
                }
            }

            return data;
        }
    }

    /// <summary>Gets the names of the root-forbidden keys.</summary>
    public static TheoryData<string> RootForbiddenKeys
    {
        get
        {
            TheoryData<string> data = [];
            foreach (string key in KeysOf(PolicyKind.ForbiddenAtRoot))
            {
                data.Add(key);
            }

            return data;
        }
    }

    /// <summary>Gets the names of the required TUN keys.</summary>
    public static TheoryData<string> RequiredTunKeyNames
    {
        get
        {
            TheoryData<string> data = [];
            foreach (string key in KeysOf(PolicyKind.ExactTunKeySet))
            {
                data.Add(key);
            }

            return data;
        }
    }

    private static Dictionary<string, Func<string, string>> RootValueShapes { get; } = new(StringComparer.Ordinal)
    {
        ["plain-scalar"] = key => $"{key}: unsafe\n",
        ["empty-null"] = key => $"{key}:\n",
        ["tilde-null"] = key => $"{key}: ~\n",
        ["empty-string"] = key => $"{key}: ''\n",
        ["empty-flow-sequence"] = key => $"{key}: []\n",
        ["empty-flow-mapping"] = key => $"{key}: {{}}\n",
        ["block-sequence"] = key => $"{key}:\n  - one\n  - two\n",
        ["block-mapping"] = key => $"{key}:\n  nested: value\n",
        ["boolean-false"] = key => $"{key}: false\n",
        ["number-zero"] = key => $"{key}: 0\n",
        ["quoted-key"] = key => $"\"{key}\": unsafe\n",
        ["single-quoted-key"] = key => $"'{key}': unsafe\n",
        ["escaped-key"] = key => $"\"{string.Concat(key.Select(c => $"\\u{(int)c:x4}"))}\": unsafe\n",
        ["explicit-key-indicator"] = key => $"? {key}\n: unsafe\n",
        ["anchored-value"] = key => $"{key}: &x unsafe\n",
        ["tagged-key"] = key => $"!!str {key}: unsafe\n",
        ["crlf-line-ending"] = key => $"{key}: unsafe\r\n",
        ["trailing-comment"] = key => $"{key}: unsafe # allowed by nobody\n",
        ["anchor-key-alias"] = key => $"alias-source: &k {key}\n*k : unsafe\n",
    };

    [Fact]
    public void EveryPrivatePolicySetIsClassified()
    {
        string[] unknown = [.. PolicySetFields()
            .Select(field => field.Name)
            .Where(name => !PolicySetKinds.ContainsKey(name))];

        Assert.True(
            unknown.Length == 0,
            "The trust validator gained policy set(s) this contract does not classify: "
            + string.Join(", ", unknown));
        foreach (string expected in PolicySetKinds.Keys)
        {
            Assert.Contains(PolicySetFields(), field => field.Name == expected);
        }
    }

    [Fact]
    public void PolicySetsAreNonEmptyAndFreeOfDuplicatesWithinAndAcrossForbiddenSets()
    {
        List<string> forbidden = [];
        foreach (PolicyKind kind in new[] { PolicyKind.ForbiddenAtRoot, PolicyKind.ForbiddenAtAnyDepth })
        {
            forbidden.AddRange(KeysOf(kind));
        }

        Assert.NotEmpty(forbidden);
        Assert.Equal(forbidden.Count, forbidden.Distinct(StringComparer.Ordinal).Count());
        Assert.All(forbidden, key => Assert.False(string.IsNullOrWhiteSpace(key)));
        Assert.All(forbidden, key => Assert.Equal(key.Trim(), key));
    }

    [Fact]
    public void ManagedBaselineIsAccepted_SoEveryRejectionBelowIsAttributableToTheInjectedKey()
    {
        Validate(Managed(BaseMixedPort));
        Validate(FlowManaged(string.Empty));
    }

    [Theory]
    [MemberData(nameof(RootForbiddenKeyAndValue))]
    public void RootForbiddenKey_IsRejectedForEveryValueShape(string key, string shape)
    {
        string injected = RootValueShapes[shape](key);

        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            Validate(Managed(BaseMixedPort + injected)));
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            Validate(Managed(injected + BaseMixedPort)));
    }

    [Theory]
    [MemberData(nameof(RootForbiddenKeys))]
    public void RootForbiddenKey_IsRejectedInFlowStyleRootMapping(string key)
    {
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            Validate(FlowManaged($", {key}: unsafe")));
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            Validate(FlowManaged($", '{key}': [a, b]")));
    }

    [Theory]
    [MemberData(nameof(RootForbiddenKeys))]
    public void RootForbiddenKey_IsRejectedWhenDuplicated(string key)
    {
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            Validate(Managed(BaseMixedPort + $"{key}: a\n{key}: b\n")));
    }

    [Theory]
    [MemberData(nameof(RootForbiddenKeys))]
    public void RootForbiddenKey_IsAcceptedWhenOnlyNestedBelowARegularSection(string key)
    {
        // The sets are root-surface policies; a proxy entry legitimately owns fields such as "port".
        string nested =
            "proxies:\n"
            + "  - name: node\n"
            + "    type: ss\n"
            + $"    {key}: nested-value\n"
            + "    server: example.invalid\n";

        Validate(Managed(BaseMixedPort + nested));
    }

    [Theory]
    [MemberData(nameof(CredentialKeyAndPlacement))]
    public void CredentialKey_IsRejectedAtEveryDepthAndContainerShape(string key, string placement)
    {
        string document = Placements(key)[placement];

        Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(document));
    }

    [Fact]
    public void CredentialKey_InsideAnAnchoredValueReusedByAliasIsStillRejected()
    {
        foreach (string key in KeysOf(PolicyKind.ForbiddenAtAnyDepth))
        {
            string document = Managed(
                BaseMixedPort
                + $"template: &t\n  {key}: secret\n"
                + "proxies:\n  - name: a\n    <<: *t\n");

            Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(document));
        }
    }

    [Theory]
    [InlineData("state-dir")]
    [InlineData("file-descriptor")]
    public void AnyDepthForbiddenKeyOutsideTheSets_IsRejectedAtEveryPlacement(string key)
    {
        foreach ((string placement, string document) in Placements(key))
        {
            Assert.True(
                Throws(() => Validate(document)),
                $"{key} was accepted at placement '{placement}'.");
        }
    }

    [Theory]
    [InlineData("false", true)]
    [InlineData("False", true)]
    [InlineData("FALSE", true)]
    [InlineData("no", true)]
    [InlineData("NO", true)]
    [InlineData("off", true)]
    [InlineData("Off", true)]
    [InlineData("0", true)]
    [InlineData("'false'", true)]
    [InlineData("true", false)]
    [InlineData("True", false)]
    [InlineData("yes", false)]
    [InlineData("on", false)]
    [InlineData("1", false)]
    [InlineData("''", false)]
    [InlineData("~", false)]
    [InlineData("null", false)]
    [InlineData("[]", false)]
    [InlineData("[false]", false)]
    [InlineData("{}", false)]
    [InlineData("{a: false}", false)]
    [InlineData("n", false)]
    [InlineData("y", false)]
    [InlineData("2", false)]
    [InlineData("falsey", false)]
    public void ConditionalWriteSurface_IsAcceptedOnlyForExplicitFalse(string value, bool accepted)
    {
        string[] documents =
        [
            Managed(BaseMixedPort + $"geo-auto-update: {value}\n"),
            Managed(BaseMixedPort + $"ntp:\n  enable: true\n  write-to-system: {value}\n"),
            Managed(BaseMixedPort + $"x:\n  - nested:\n      geo-auto-update: {value}\n"),
        ];

        foreach (string document in documents)
        {
            if (accepted)
            {
                Validate(document);
            }
            else
            {
                Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(document));
            }
        }
    }

    [Theory]
    [InlineData("ssh", true)]
    [InlineData("SSH", true)]
    [InlineData("Ssh", true)]
    [InlineData("'ssh'", true)]
    [InlineData("wireguard", false)]
    [InlineData("ss", false)]
    [InlineData("sshx", false)]
    public void PrivateKey_IsForbiddenOnlyForSshProxiesRegardlessOfKeyOrder(string type, bool rejected)
    {
        string[] mappings =
        [
            $"  - name: p\n    type: {type}\n    private-key: C:/Users/x/id\n",
            $"  - name: p\n    private-key: C:/Users/x/id\n    type: {type}\n",
            $"  - {{name: p, type: {type}, private-key: 'C:/Users/x/id'}}\n",
        ];

        foreach (string mapping in mappings)
        {
            string document = Managed(BaseMixedPort + "proxies:\n" + mapping);
            if (rejected)
            {
                Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(document));
            }
            else
            {
                Validate(document);
            }
        }
    }

    [Theory]
    [InlineData("<<: *a")]
    [InlineData("'<<': *a")]
    [InlineData("\"<<\": [*a]")]
    public void MergeKey_IsRejectedAtEveryDepth(string mergeLine)
    {
        string[] documents =
        [
            Managed(BaseMixedPort + $"base: &a {{k: v}}\n{mergeLine}\n"),
            Managed(BaseMixedPort + $"base: &a {{k: v}}\nproxies:\n  - name: x\n    {mergeLine}\n"),
            Managed(BaseMixedPort + $"base: &a {{k: v}}\nd1:\n  d2:\n    d3:\n      {mergeLine}\n"),
            Managed(BaseMixedPort + $"base: &a {{k: v}}\nflow: {{n: 1, {mergeLine}}}\n"),
        ];

        foreach (string document in documents)
        {
            Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(document));
        }
    }

    [Theory]
    [MemberData(nameof(RequiredTunKeyNames))]
    public void RequiredTunKey_CannotBeRemovedAddedOrWeakened(string key)
    {
        Dictionary<string, (string Canonical, string[] Hostile)> tun = TunValueTable();
        Assert.True(
            tun.ContainsKey(key),
            $"RequiredTunKeys gained '{key}' but this contract has no canonical/hostile values for it.");

        // Removing the key.
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            Validate(ManagedWithTun(tun
                .Where(pair => pair.Key != key)
                .ToDictionary(pair => pair.Key, pair => pair.Value.Canonical))));

        // Each hostile replacement value.
        foreach (string hostile in tun[key].Hostile)
        {
            Dictionary<string, string> weakened = tun.ToDictionary(pair => pair.Key, pair => pair.Value.Canonical);
            weakened[key] = hostile;
            Assert.True(
                Throws(() => Validate(ManagedWithTun(weakened))),
                $"tun.{key}: {hostile} was accepted.");
        }

        // The canonical tuple itself stays valid so the checks above are attributable.
        Validate(ManagedWithTun(tun.ToDictionary(pair => pair.Key, pair => pair.Value.Canonical)));
    }

    [Fact]
    public void Tun_ExtraKeyIsRejected()
    {
        Dictionary<string, string> canonical = TunValueTable().ToDictionary(pair => pair.Key, pair => pair.Value.Canonical);
        foreach (string extra in new[] { "device", "mtu", "route-address", "file-descriptor", "inet4-address", "Enable" })
        {
            Dictionary<string, string> withExtra = new(canonical, StringComparer.Ordinal) { [extra] = "1" };
            Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(ManagedWithTun(withExtra)));
        }
    }

    [Theory]
    [InlineData("external-controller")]
    [InlineData("secret")]
    [InlineData("mixed-port")]
    [InlineData("allow-lan")]
    [InlineData("bind-address")]
    [InlineData("mode")]
    [InlineData("tun")]
    public void ManagedRootKey_IsRequired(string key)
    {
        string complete = FlowManaged(string.Empty);
        Validate(complete);

        string removed = RemoveFlowEntry(complete, key);

        Assert.NotEqual(complete, removed);
        Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(removed));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("65536")]
    [InlineData("-1")]
    [InlineData("+80")]
    [InlineData("1e3")]
    [InlineData("80.0")]
    [InlineData("0x50")]
    [InlineData("'８０'")]
    [InlineData("'٨٠'")]
    [InlineData("''")]
    [InlineData("' 80'")]
    [InlineData("99999999999")]
    [InlineData("[80]")]
    [InlineData("{a: 80}")]
    public void MixedPort_RejectsEverythingButAnAsciiPortNumber(string hostile)
    {
        Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
            Validate(Managed($"mixed-port: {hostile}\n")));
    }

    [Theory]
    [InlineData("1")]
    [InlineData("80")]
    [InlineData("7890")]
    [InlineData("65535")]
    public void MixedPort_AcceptsTheWholeValidRange(string port)
    {
        Validate(Managed($"mixed-port: {port}\n"));
    }

    [Theory]
    [InlineData("allow-lan", "true")]
    [InlineData("allow-lan", "False")]
    [InlineData("allow-lan", "'false '")]
    [InlineData("allow-lan", "0")]
    [InlineData("bind-address", "'*'")]
    [InlineData("bind-address", "localhost")]
    [InlineData("bind-address", "'::1'")]
    [InlineData("bind-address", "127.0.0.2")]
    [InlineData("mode", "Rule")]
    [InlineData("mode", "direct")]
    [InlineData("mode", "script")]
    [InlineData("mode", "''")]
    [InlineData("mode", "[rule]")]
    [InlineData("external-controller", "127.0.0.1:9091")]
    [InlineData("external-controller", "0.0.0.0:9090")]
    [InlineData("external-controller", "localhost:9090")]
    [InlineData("external-controller", "'127.0.0.1:9090 '")]
    [InlineData("external-controller", "[127.0.0.1:9090]")]
    [InlineData("secret", "''")]
    [InlineData("secret", "short")]
    public void ManagedAuthorityValue_CannotBeAnythingButTheManagedConstant(string key, string hostile)
    {
        string baseline = Managed(BaseMixedPort);
        string[] lines = baseline.Split('\n');
        int index = Array.FindIndex(lines, line => line.StartsWith(key + ":", StringComparison.Ordinal));
        Assert.True(index >= 0);
        lines[index] = $"{key}: {hostile}";

        Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(string.Join('\n', lines)));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("- a")]
    [InlineData("scalar")]
    [InlineData("---\nmixed-port: 1\n---\nmixed-port: 2")]
    [InlineData("")]
    [InlineData("# only a comment")]
    [InlineData("~")]
    public void NonMappingOrMultiDocumentRoot_IsRejected(string text)
    {
        Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(text));
    }

    [Fact]
    public void ValidateRejectsEveryProviderSectionThatIsNotAMapping()
    {
        foreach (string section in KeysOf(PolicyKind.ProviderSections))
        {
            foreach (string value in new[] { "[]", "scalar", "- a", "~", "''" })
            {
                Assert.True(
                    Throws(() => Validate(Managed(BaseMixedPort + $"{section}: {value}\n"))),
                    $"{section}: {value} was accepted.");
            }
        }
    }

    [Fact]
    public void ProviderSection_CountIsBounded()
    {
        foreach (string section in KeysOf(PolicyKind.ProviderSections))
        {
            StringBuilder builder = new(BaseMixedPort);
            builder.Append(section).Append(":\n");
            for (int index = 0; index < 513; index++)
            {
                builder.Append("  p").Append(index).Append(":\n    type: inline\n");
            }

            Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(Managed(builder.ToString())));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("a\\u0007b")]
    public void ProviderName_MustBeNonEmptyNonControlText(string name)
    {
        foreach (string section in KeysOf(PolicyKind.ProviderSections))
        {
            string document = Managed(BaseMixedPort + $"{section}:\n  \"{name}\":\n    type: inline\n");
            Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(document));
        }
    }

    [Fact]
    public void ProviderName_LengthIsBounded()
    {
        foreach (string section in KeysOf(PolicyKind.ProviderSections))
        {
            Validate(Managed(BaseMixedPort + $"{section}:\n  {new string('n', 512)}:\n    type: inline\n"));
            Assert.Throws<MihomoServiceConfigurationTrustException>(() =>
                Validate(Managed(BaseMixedPort + $"{section}:\n  {new string('n', 513)}:\n    type: inline\n")));
        }
    }

    [Theory]
    [InlineData("type: inline\n    path: a.yaml")]
    [InlineData("type: http")]
    [InlineData("type: http\n    url: ftp://example.invalid/x")]
    [InlineData("type: http\n    url: file:///c:/x")]
    [InlineData("type: http\n    url: //example.invalid/x")]
    [InlineData("type: http\n    url: /relative")]
    [InlineData("type: http\n    url: https://u:p@example.invalid/x")]
    [InlineData("type: http\n    url: https://example.invalid/x\n    path: ../escape.yaml")]
    [InlineData("type: file")]
    [InlineData("type: file\n    path: a.yaml\n    url: https://example.invalid/x")]
    [InlineData("type: file\n    path: /abs.yaml")]
    [InlineData("type: file\n    path: ..\\\\up.yaml")]
    [InlineData("type: script")]
    [InlineData("type: ''")]
    [InlineData("path: a.yaml")]
    [InlineData("type: [file]")]
    public void ProviderDefinition_ShapesThatCouldReachOutsideTheRuntimeAreRejected(string body)
    {
        foreach (string section in KeysOf(PolicyKind.ProviderSections))
        {
            string document = Managed(BaseMixedPort + $"{section}:\n  p:\n    {body}\n");
            Assert.Throws<MihomoServiceConfigurationTrustException>(() => Validate(document));
        }
    }

    [Fact]
    public void ProviderRelativePath_RejectsEveryNonCanonicalOrReservedForm()
    {
        List<string> hostile =
        [
            "", " ", ".", "..", "./a", "../a", "a/..", "a/../b", "a//b", "/a", "\\a", "a/", "a\\",
            "C:", "C:\\a", "C:a", "\\\\server\\share", "//server/share", "a:b", "a<b", "a>b", "a\"b",
            "a|b", "a?b", "a*b", "a\0b", "trail.", "trail ", "dir./file", "dir /file", "a/ /b",
            new string('a', 1025), "a/" + new string('b', 1030),
        ];
        List<string> devices = ["CON", "PRN", "AUX", "NUL"];
        for (int number = 1; number <= 9; number++)
        {
            devices.Add($"COM{number}");
            devices.Add($"LPT{number}");
        }

        foreach (string device in devices)
        {
            hostile.Add(device);
            hostile.Add(device.ToLowerInvariant());
            hostile.Add($"{device}.yaml");
            hostile.Add($"dir/{device}");
            hostile.Add($"dir/{device}.tar.yaml");
        }

        foreach (string path in hostile)
        {
            Assert.True(
                Throws(() => MihomoServiceConfigurationTrustValidator.ValidateProviderRelativePath(path)),
                $"Provider path '{path.Replace("\0", "\\0", StringComparison.Ordinal)}' was accepted.");
        }
    }

    [Theory]
    [InlineData("a.yaml")]
    [InlineData("dir/a.yaml")]
    [InlineData("dir\\a.yaml")]
    [InlineData("规则/文件.yaml")]
    [InlineData("with space/in name.yaml")]
    [InlineData("COM10.yaml")]
    [InlineData("CONSOLE.yaml")]
    [InlineData("comx/file")]
    [InlineData("a.b.c")]
    public void ProviderRelativePath_AcceptsOrdinaryRelativePaths(string path)
    {
        string[] components = MihomoServiceConfigurationTrustValidator.ValidateProviderRelativePath(path);

        Assert.NotEmpty(components);
        Assert.All(components, component => Assert.False(string.IsNullOrWhiteSpace(component)));
        Assert.DoesNotContain(components, component => component is "." or "..");
    }

    [Fact]
    public void ProviderRelativePath_NeverYieldsAComponentThatCanEscape_ForGeneratedPaths()
    {
        Random random = new(20261109);
        char[] alphabet = "ab./\\: *?<>|\0 \t-_é中".ToCharArray();
        for (int round = 0; round < 3000; round++)
        {
            int length = random.Next(1, 24);
            string candidate = new(Enumerable.Range(0, length).Select(_ => alphabet[random.Next(alphabet.Length)]).ToArray());
            string[] components;
            try
            {
                components = MihomoServiceConfigurationTrustValidator.ValidateProviderRelativePath(candidate);
            }
            catch (Exception exception) when (exception is MihomoServiceConfigurationTrustException or ArgumentException)
            {
                continue;
            }

            Assert.NotEmpty(components);
            foreach (string component in components)
            {
                Assert.False(string.IsNullOrWhiteSpace(component));
                Assert.NotEqual(".", component);
                Assert.NotEqual("..", component);
                Assert.False(component.EndsWith('.') || component.EndsWith(' '));
                Assert.DoesNotContain(component, c => "<>:\"|?*\0/\\".Contains(c, StringComparison.Ordinal));
            }

            Assert.False(Path.IsPathRooted(candidate));
        }
    }

    private static bool Throws(Action action)
    {
        return Record.Exception(action) is MihomoServiceConfigurationTrustException or ArgumentException;
    }

    private static void Validate(string text)
    {
        MihomoServiceConfigurationTrustValidator.ValidatePolicyText(text);
    }

    private static string Managed(string content)
    {
        return MihomoServiceTestSupport.BuildManagedServiceConfiguration(content);
    }

    private static IEnumerable<FieldInfo> PolicySetFields()
    {
        return typeof(MihomoServiceConfigurationTrustValidator)
            .GetFields(BindingFlags.NonPublic | BindingFlags.Static)
            .Where(field => field.FieldType == typeof(HashSet<string>));
    }

    private static IEnumerable<string> KeysOf(PolicyKind kind)
    {
        foreach (FieldInfo field in PolicySetFields())
        {
            if (PolicySetKinds.TryGetValue(field.Name, out PolicyKind actual) && actual == kind)
            {
                foreach (string key in (HashSet<string>)field.GetValue(null)!)
                {
                    yield return key;
                }
            }
        }
    }

    private static Dictionary<string, string> Placements(string key)
    {
        string k = key;
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["root-block"] = Managed(BaseMixedPort + $"{k}: v\n"),
            ["nested-mapping"] = Managed(BaseMixedPort + $"outer:\n  {k}: v\n"),
            ["sequence-item-mapping"] = Managed(BaseMixedPort + $"proxies:\n  - name: a\n    {k}: v\n"),
            ["flow-mapping-in-sequence"] = Managed(BaseMixedPort + $"proxies:\n  - {{name: a, {k}: v}}\n"),
            ["deep-nesting"] = Managed(BaseMixedPort + DeepMapping(40, $"{k}: v")),
            ["sequence-of-sequences"] = Managed(BaseMixedPort + $"outer:\n  - - - {{{k}: v}}\n"),
            ["inside-provider"] = Managed(BaseMixedPort + $"proxy-providers:\n  p:\n    type: inline\n    {k}: v\n"),
            ["quoted-key"] = Managed(BaseMixedPort + $"outer:\n  \"{k}\": v\n"),
            ["null-valued"] = Managed(BaseMixedPort + $"outer:\n  {k}:\n"),
            ["mapping-valued"] = Managed(BaseMixedPort + $"outer:\n  {k}:\n    sub: 1\n"),
        };
    }

    private static string DeepMapping(int depth, string leaf)
    {
        StringBuilder builder = new();
        for (int level = 0; level < depth; level++)
        {
            builder.Append(' ', level * 2).Append('l').Append(level).Append(":\n");
        }

        builder.Append(' ', depth * 2).Append(leaf).Append('\n');
        return builder.ToString();
    }

    private static Dictionary<string, (string Canonical, string[] Hostile)> TunValueTable() => new(StringComparer.Ordinal)
    {
        ["enable"] = ("true", ["false", "'true '", "1", "yes", "[true]", "''", "~"]),
        ["stack"] = ("system", ["mixed", "gvisor", "System", "''", "[system]"]),
        ["auto-route"] = ("true", ["false", "0", "~", "[true]"]),
        ["auto-detect-interface"] = ("true", ["false", "0", "~", "[true]"]),
        ["strict-route"] = ("false", ["true", "1", "yes", "~", "[false]"]),
        ["dns-hijack"] = ("['any:53']", ["[]", "['any:53', '8.8.8.8:53']", "['0.0.0.0:53']", "'any:53'", "['tcp://any:53']", "['ANY:53']", "{any: '53'}"]),
    };

    private static string ManagedWithTun(IReadOnlyDictionary<string, string> tun)
    {
        string entries = string.Join(", ", tun.Select(pair => $"{pair.Key}: {pair.Value}"));
        return FlowManaged(string.Empty, $"{{{entries}}}");
    }

    /// <summary>Builds a complete managed document in flow style, optionally with extra entries or a replacement tun value.</summary>
    private static string FlowManaged(string extraEntries, string? tunValue = null)
    {
        string tun = tunValue
            ?? "{enable: true, stack: system, auto-route: true, auto-detect-interface: true, strict-route: false, dns-hijack: ['any:53']}";
        return "{external-controller: '127.0.0.1:9090', "
            + $"secret: '{MihomoServiceTestSupport.Token}', "
            + "allow-lan: false, bind-address: 127.0.0.1, mode: rule, mixed-port: 7890, "
            + $"tun: {tun}{extraEntries}}}";
    }

    private static string RemoveFlowEntry(string flowDocument, string key)
    {
        // The managed flow document is generated above with entries separated at depth zero by ", ".
        int start = flowDocument.IndexOf(key + ": ", StringComparison.Ordinal);
        Assert.True(start >= 0);
        int depth = 0;
        int index = start;
        while (index < flowDocument.Length)
        {
            char character = flowDocument[index];
            if (character is '{' or '[')
            {
                depth++;
            }
            else if (character is '}' or ']')
            {
                if (depth == 0)
                {
                    break;
                }

                depth--;
            }
            else if (character == ',' && depth == 0)
            {
                index += 2;
                return flowDocument.Remove(start, index - start);
            }

            index++;
        }

        // Last entry: also remove the separator before it.
        return flowDocument.Remove(start - 2, index - (start - 2));
    }
}
