using System;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Tests minimum mihomo profile section validation behavior.</summary>
/// <remarks>
/// Invariants: Tests inspect in-memory configuration text only and never start mihomo.
/// Thread safety: xUnit may run tests concurrently; validator methods are stateless.
/// Side effects: None.
/// </remarks>
public sealed class MihomoProfileShapeValidatorTests
{
    /// <summary>Verifies inline proxy source, proxy groups, and rules pass shape validation.</summary>
    [Fact]
    public void Validate_ProfileWithProxiesGroupsAndRules_Completes()
    {
        const string Configuration = """
            proxies: []
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            rules:
              - MATCH,DIRECT
            """;

        MihomoProfileShapeValidator.Validate(Configuration);
    }

    /// <summary>Verifies provider-only profiles pass shape validation.</summary>
    [Fact]
    public void Validate_ProfileWithProvidersGroupsAndRules_Completes()
    {
        const string Configuration = """
            proxy-providers:
              airport:
                type: http
                url: https://example.invalid/sub.yaml
            proxy-groups:
              - name: GLOBAL
                type: select
                use:
                  - airport
            rules:
              - MATCH,GLOBAL
            """;

        MihomoProfileShapeValidator.Validate(Configuration);
    }

    /// <summary>Verifies missing routing rules fail shape validation.</summary>
    [Fact]
    public void Validate_ProfileWithoutRules_Throws()
    {
        const string Configuration = """
            proxies: []
            proxy-groups:
              - name: GLOBAL
                type: select
                proxies:
                  - DIRECT
            """;

        ProfileValidationException error = Assert.Throws<ProfileValidationException>(() => MihomoProfileShapeValidator.Validate(Configuration));
        Assert.Equal(MissingProfileSections.Rules, error.MissingSections);
        Assert.Equal("rules", error.GetMissingSectionNames());
    }

    /// <summary>Verifies indented pseudo sections are not accepted as top-level sections.</summary>
    [Fact]
    public void Validate_IndentedPseudoSections_Throws()
    {
        const string Configuration = """
            metadata:
              proxies: []
              proxy-groups: []
              rules: []
            """;

        ProfileValidationException error = Assert.Throws<ProfileValidationException>(() => MihomoProfileShapeValidator.Validate(Configuration));
        Assert.Equal(MissingProfileSections.ProxySource | MissingProfileSections.ProxyGroups | MissingProfileSections.Rules, error.MissingSections);
        Assert.Equal("proxies / proxy-providers, proxy-groups, rules", error.GetMissingSectionNames());
    }

    [Fact]
    public void Validate_ProfileWithoutGroups_ReportsOnlyMissingGroups()
    {
        ProfileValidationException error = Assert.Throws<ProfileValidationException>(() =>
            MihomoProfileShapeValidator.Validate("proxies: []\nrules:\n  - MATCH,DIRECT\n# private configuration content"));
        Assert.Equal(MissingProfileSections.ProxyGroups, error.MissingSections);
        Assert.Equal("proxy-groups", error.GetMissingSectionNames());
        Assert.DoesNotContain("private", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_ProfileWithoutSource_DescribesAlternativeKeys()
    {
        ProfileValidationException error = Assert.Throws<ProfileValidationException>(() =>
            MihomoProfileShapeValidator.Validate("proxy-groups: []\nrules: []"));
        Assert.Equal(MissingProfileSections.ProxySource, error.MissingSections);
        Assert.Equal("proxies / proxy-providers", error.GetMissingSectionNames());
    }
}
