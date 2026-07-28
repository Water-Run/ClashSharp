using System;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Pure presentation mapper backed by an injected UI text policy.</summary>
internal sealed class ModelDisplayMapper : IModelDisplayMapper
{
    private readonly Func<string, string> _displayTextFilter;

    public ModelDisplayMapper(Func<string, string> displayTextFilter)
    {
        _displayTextFilter = displayTextFilter
            ?? throw new ArgumentNullException(nameof(displayTextFilter));
    }

    public string MapText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return _displayTextFilter(text);
    }

    public ConfigurationProfileDisplay Map(ConfigurationProfile profile)
    {
        return new ConfigurationProfileDisplay(
            profile,
            MapText(profile.Name),
            MapText(profile.SourceName),
            MapText(profile.Status));
    }

    public ProfileSubscriptionLinkDisplay Map(ProfileSubscriptionLink link)
    {
        return new ProfileSubscriptionLinkDisplay(
            link,
            MapText(link.Name),
            MapText(link.Uri),
            MapText(link.Status));
    }

    public ProxyNodeDisplay Map(ProxyNode node)
    {
        return new ProxyNodeDisplay(node, MapText(node.Name));
    }

    public MihomoProxyGroupDisplay Map(MihomoProxyGroup group)
    {
        return new MihomoProxyGroupDisplay(
            group,
            MapText(group.Name),
            MapText(group.CurrentSelection));
    }

    public MihomoProviderResourceDisplay Map(MihomoProviderResource provider)
    {
        return new MihomoProviderResourceDisplay(provider, MapText(provider.Name));
    }

    public RulePreviewDisplay Map(RulePreview rule)
    {
        return new RulePreviewDisplay(
            rule,
            MapText(rule.ProviderName),
            MapText(rule.Payload));
    }
}
