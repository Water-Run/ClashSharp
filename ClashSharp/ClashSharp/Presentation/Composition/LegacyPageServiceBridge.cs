using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Service;

namespace ClashSharp.Presentation.Composition;

/// <summary>Provides the transitional bridge from page composition to legacy process-wide services.</summary>
/// <remarks>
/// The bridge is intentionally lazy: accessing one page dependency does not initialize unrelated services.
/// New view code must depend on a page composition contract instead of accessing this bridge directly.
/// A host-owned page factory can replace this bridge without changing XAML code-behind.
/// </remarks>
internal static class LegacyPageServiceBridge
{
    public static AppSettingsService Settings => AppSettingsService.Instance;

    public static CoreConfigurationService CoreConfiguration => CoreConfigurationService.Instance;

    public static LocalizationService Localization => LocalizationService.Instance;

    public static LogStorageService LogStorage => LogStorageService.Instance;

    public static MainlandChinaTextDisplayService MainlandChinaTextDisplay => MainlandChinaTextDisplayService.Instance;

    public static MihomoConnectionService MihomoConnections => MihomoConnectionService.Instance;

    public static MihomoControllerClient MihomoController => MihomoControllerClient.Instance;

    public static MihomoCoreService MihomoCore => MihomoCoreService.Instance;

    public static ProfileCatalogService Profiles => ProfileCatalogService.Instance;

    public static ProxyLatencyService ProxyLatency => ProxyLatencyService.Instance;

    public static ProxyNodeCatalogService ProxyNodes => ProxyNodeCatalogService.Instance;

    public static RuleCatalogService Rules => RuleCatalogService.Instance;

    public static IApplicationErrorSink CreateErrorSink()
    {
        return ApplicationErrorSink.CreateDefault();
    }
}
