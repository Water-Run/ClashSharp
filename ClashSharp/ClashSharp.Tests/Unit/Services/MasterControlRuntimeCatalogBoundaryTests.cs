using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

public sealed class MasterControlRuntimeCatalogBoundaryTests
{
    public static TheoryData<string?> ProfileCatalogCases =>
        new()
        {
            { null },
            { "{ not-json" },
            {
                """
                {
                  "Profiles": [{ "Id": "sample-rule-profile" }],
                  "Links": [{ "Id": "link-one" }]
                }
                """
            },
            {
                """
                {
                  "Profiles": [
                    { "Id": "built-in-direct" },
                    { "Id": "work-profile" }
                  ],
                  "Links": [{ "Id": "link-one" }, { "Id": "link-two" }]
                }
                """
            },
        };

    [Theory]
    [MemberData(nameof(ProfileCatalogCases))]
    public void GetSummary_MatchesExistingCatalogRecoveryAndBuiltInSemantics(
        string? catalogJson)
    {
        string testDirectory = CreateTestDirectory();

        try
        {
            string expectedPath = Path.Combine(testDirectory, "expected", "ProfileCatalog.json");
            string actualPath = Path.Combine(testDirectory, "actual", "ProfileCatalog.json");
            WriteCatalog(expectedPath, catalogJson);
            WriteCatalog(actualPath, catalogJson);
            ProfileCatalogService expectedService = CreateProfileCatalog(expectedPath);
            ProfileCatalogService actualService = CreateProfileCatalog(actualPath);

            int expectedProfiles = expectedService.GetProfiles().Count;
            int expectedLinks = expectedService.GetSubscriptionLinks().Count;
            ProfileCatalogSummary actual = actualService.GetSummary(
                new ProfileCatalogFallbackStrings("Direct", "Available"));

            Assert.Equal(expectedProfiles, actual.ProfileCount);
            Assert.Equal(expectedLinks, actual.SubscriptionCount);
        }
        finally
        {
            Directory.Delete(testDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("rules:\n  - MATCH,*,DIRECT")]
    [InlineData(
        """
        proxies:
          - name: node-one
            type: http
            server: one.example
            port: 80
          - name: node-two
            type: socks5
            server: two.example
            port: 1080
        rules:
          - DOMAIN,one.example,DIRECT
          - MATCH,*,REJECT
        """)]
    public void PureProfileCounts_MatchExistingCatalogFallbackSemantics(string? configurationText)
    {
        MihomoProfileParserService parser = new(
            new FixedProfileTextSource(configurationText),
            static region => new RegionMetadata(region, region, region),
            static key => key);
        ProxyNodeCatalogService nodes = new(
            new ParserNodeSource(parser),
            static region => new RegionMetadata(region, region, region));
        RuleCatalogService rules = new(
            new ParserRuleSource(parser),
            new EmptyRuleHitStorage(),
            static key => key);

        int expectedNodeCount = nodes.GetNodes().Count;
        int expectedRuleCount = rules.GetRules().Count;

        Assert.Equal(expectedNodeCount, ProxyNodeCatalogService.CountNodes(configurationText));
        Assert.Equal(expectedRuleCount, RuleCatalogService.CountRules(configurationText));
    }

    private static ProfileCatalogService CreateProfileCatalog(string path)
    {
        return new ProfileCatalogService(
            path,
            Path.Combine(Path.GetDirectoryName(path)!, "mihomo", "history"),
            new FixedProfileSettings(),
            new UnusedProfileCoreConfiguration(),
            new UnusedProfileCatalogRuntime(),
            new NullProfileLog(),
            static key => key switch
            {
                "ProfileCatalog.BuiltInDirect.Name" => "Direct",
                "ProfileCatalog.Status.Available" => "Available",
                _ => key,
            },
            UncoordinatedProfileCatalogMutationCoordinator.Instance);
    }

    private sealed class UnusedProfileCatalogRuntime : IProfileCatalogRuntime
    {
        public Task<bool> ApplyProfileAsync(string profileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProfileCatalogRuntimeImportResult> ImportAndApplyProfileAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<bool> DeleteImportedProfileAsync(string profileId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static void WriteCatalog(string path, string? catalogJson)
    {
        if (catalogJson is null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, catalogJson);
    }

    private static string CreateTestDirectory()
    {
        string path = Path.Combine(
            Path.GetTempPath(),
            "ClashSharp.Tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private sealed class FixedProfileSettings : IProfileCatalogSettings
    {
        public string ActiveProfileId { get; set; } = ProfileCatalogIds.BuiltInDirect;
    }

    private sealed class UnusedProfileCoreConfiguration : IProfileCatalogCoreConfiguration
    {
        public Task<ProfileImportResult> ImportProfileConfigurationAsync(
            string profileId,
            string profileName,
            string configurationText,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public CoreConfigurationState EnsureDefaultConfiguration()
        {
            throw new NotSupportedException();
        }

        public Task<string?> ReadImportedProfileConfigurationAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }

        public Task<ProfileImportResult> ValidateImportedProfileAsync(
            string profileId,
            CancellationToken cancellationToken)
        {
            throw new NotSupportedException();
        }
    }

    private sealed class NullProfileLog : IProfileCatalogLog
    {
        public void AppendLog(string level, string category, string message, string? detail)
        {
        }
    }

    private sealed class FixedProfileTextSource(string? text) : IMihomoProfileTextSource
    {
        public string? TryReadActiveProfileText()
        {
            return text;
        }
    }

    private sealed class ParserNodeSource(MihomoProfileParserService parser) : IProxyNodeCatalogProfileNodes
    {
        public IReadOnlyList<ProxyNode> ParseActiveProfileNodes()
        {
            return parser.ParseActiveProfileNodes();
        }
    }

    private sealed class ParserRuleSource(MihomoProfileParserService parser) : IRuleCatalogProfileRules
    {
        public IReadOnlyList<RulePreview> ParseActiveProfileRules()
        {
            return parser.ParseActiveProfileRules();
        }
    }

    private sealed class EmptyRuleHitStorage : IRuleCatalogHitStorage
    {
        public void EnsureRuleHitRows(IReadOnlyList<RulePreview> rules)
        {
        }

        public IReadOnlyDictionary<string, long> GetRuleHitCounts()
        {
            return new Dictionary<string, long>();
        }
    }
}
