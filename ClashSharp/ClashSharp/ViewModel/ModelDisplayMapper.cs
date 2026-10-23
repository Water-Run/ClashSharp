using System;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Pure presentation mapper backed by an injected UI text policy.</summary>
internal sealed class ModelDisplayMapper : IModelDisplayMapper
{
    private readonly Func<string, string> _displayTextFilter;
    private readonly DisplayFormatter _formatter;
    private readonly Func<string, Func<string, string>, string>? _profileStatusLocalizer;
    private readonly Func<string, Func<string, string>, string>? _subscriptionStatusLocalizer;
    public DisplayFormatter Formatter => _formatter;

    public ModelDisplayMapper(Func<string, string> displayTextFilter, DisplayFormatter? formatter = null,
        Func<string, Func<string, string>, string>? profileStatusLocalizer = null,
        Func<string, Func<string, string>, string>? subscriptionStatusLocalizer = null)
    {
        _displayTextFilter = displayTextFilter
            ?? throw new ArgumentNullException(nameof(displayTextFilter));
        _formatter = formatter ?? new();
        _profileStatusLocalizer = profileStatusLocalizer;
        _subscriptionStatusLocalizer = subscriptionStatusLocalizer;
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
            MapText(profile.Status))
        {
            Formatter = _formatter,
            StatusTextFactory = _profileStatusLocalizer is null ? null
                : getString => MapText(_profileStatusLocalizer(profile.Status, getString)),
        };
    }

    public ProfileSubscriptionLinkDisplay Map(ProfileSubscriptionLink link)
    {
        return new ProfileSubscriptionLinkDisplay(
            link,
            MapText(link.Name),
            MapText(link.Uri),
            MapText(link.Status))
        {
            Formatter = _formatter,
            StatusTextFactory = _subscriptionStatusLocalizer is null ? null
                : getString => MapText(_subscriptionStatusLocalizer(link.Status, getString)),
        };
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
            MapText(group.CurrentSelection))
        { TypeDisplay = MapText(group.Type) };
    }

    public MihomoProviderResourceDisplay Map(MihomoProviderResource provider)
    {
        return new MihomoProviderResourceDisplay(provider, MapText(provider.Name)) { Formatter = _formatter };
    }

    public RulePreviewDisplay Map(RulePreview rule)
    {
        return new RulePreviewDisplay(
            rule,
            MapText(rule.ProviderName),
            MapText(rule.Payload));
    }
}
