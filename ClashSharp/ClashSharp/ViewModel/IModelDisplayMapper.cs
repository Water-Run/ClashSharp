using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Maps persisted and runtime models to presentation-owned bindable rows.</summary>
internal interface IModelDisplayMapper
{
    string MapText(string text);

    ConfigurationProfileDisplay Map(ConfigurationProfile profile);

    ProfileSubscriptionLinkDisplay Map(ProfileSubscriptionLink link);

    ProxyNodeDisplay Map(ProxyNode node);

    MihomoProxyGroupDisplay Map(MihomoProxyGroup group);

    MihomoProviderResourceDisplay Map(MihomoProviderResource provider);

    RulePreviewDisplay Map(RulePreview rule);
}
