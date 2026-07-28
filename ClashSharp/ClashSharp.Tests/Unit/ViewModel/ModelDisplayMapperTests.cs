using System.Text.Json;
using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Verifies UI filtering is applied by presentation mapping without mutating models.</summary>
public sealed class ModelDisplayMapperTests
{
    [Fact]
    public void Map_AppliesInjectedPolicyToAllModelDisplayFields()
    {
        ModelDisplayMapper mapper = new(static text => $"display:{text}");
        ConfigurationProfile profile = new(
            "profile-id",
            "profile-name",
            "source-name",
            "profile-status",
            DateTimeOffset.UnixEpoch,
            2,
            3,
            true);
        ProfileSubscriptionLink link = new(
            "link-id",
            "link-name",
            "https://example.com/subscription",
            true,
            24,
            DateTimeOffset.UnixEpoch,
            "link-status");
        ProxyNode node = new(
            "node-name",
            "HTTPS",
            new RegionMetadata("US", "United States", "us"),
            12);
        MihomoProxyGroup group = new(
            "group-name",
            "Selector",
            "selected-node",
            ["selected-node"]);
        MihomoProviderResource provider = new(
            "provider-name",
            MihomoProviderKind.Proxy,
            "HTTP",
            string.Empty,
            1,
            DateTimeOffset.UnixEpoch);
        RulePreview rule = new(
            "rule-provider",
            "DOMAIN",
            "rule-payload",
            "PROXY",
            4);

        ConfigurationProfileDisplay profileDisplay = mapper.Map(profile);
        ProfileSubscriptionLinkDisplay linkDisplay = mapper.Map(link);
        ProxyNodeDisplay nodeDisplay = mapper.Map(node);
        MihomoProxyGroupDisplay groupDisplay = mapper.Map(group);
        MihomoProviderResourceDisplay providerDisplay = mapper.Map(provider);
        RulePreviewDisplay ruleDisplay = mapper.Map(rule);

        Assert.Equal(profile, profileDisplay.Model);
        Assert.Equal("display:profile-name", profileDisplay.NameDisplay);
        Assert.Equal("display:source-name", profileDisplay.SourceNameDisplay);
        Assert.Equal("display:profile-status", profileDisplay.StatusDisplay);

        Assert.Equal(link, linkDisplay.Model);
        Assert.Equal("display:link-name", linkDisplay.NameDisplay);
        Assert.Equal("display:https://example.com/subscription", linkDisplay.UriDisplay);
        Assert.Equal("display:link-status", linkDisplay.StatusDisplay);

        Assert.Equal(node, nodeDisplay.Model);
        Assert.Equal("display:node-name", nodeDisplay.NameDisplay);

        Assert.Equal(group, groupDisplay.Model);
        Assert.Equal("display:group-name", groupDisplay.NameDisplay);
        Assert.Equal("display:selected-node", groupDisplay.CurrentSelectionDisplay);

        Assert.Equal(provider, providerDisplay.Model);
        Assert.Equal("display:provider-name", providerDisplay.NameDisplay);

        Assert.Equal(rule, ruleDisplay.Model);
        Assert.Equal("display:rule-provider", ruleDisplay.ProviderNameDisplay);
        Assert.Equal("display:rule-payload", ruleDisplay.PayloadDisplay);
    }

    [Fact]
    public void PersistedModels_KeepCanonicalShapeAndIgnoreLegacyDisplayProperties()
    {
        ConfigurationProfile profile = new(
            "profile-id",
            "profile-name",
            "source-name",
            "profile-status",
            DateTimeOffset.UnixEpoch,
            2,
            3,
            true);
        ProfileSubscriptionLink link = new(
            "link-id",
            "link-name",
            "https://example.com/subscription",
            true,
            24,
            DateTimeOffset.UnixEpoch,
            "link-status");

        string profileJson = JsonSerializer.Serialize(profile);
        string linkJson = JsonSerializer.Serialize(link);
        string legacyProfileJson = profileJson[..^1] + ",\"NameDisplay\":\"stale-profile\"}";
        string legacyLinkJson = linkJson[..^1] + ",\"UriDisplay\":\"stale-uri\"}";

        ConfigurationProfile restoredProfile =
            JsonSerializer.Deserialize<ConfigurationProfile>(legacyProfileJson);
        ProfileSubscriptionLink restoredLink =
            JsonSerializer.Deserialize<ProfileSubscriptionLink>(legacyLinkJson);

        Assert.DoesNotContain("Display", profileJson, StringComparison.Ordinal);
        Assert.DoesNotContain("Display", linkJson, StringComparison.Ordinal);
        Assert.Equal(profile, restoredProfile);
        Assert.Equal(link, restoredLink);
    }
}
