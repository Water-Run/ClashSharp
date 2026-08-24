using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Service;

namespace ClashSharp.Presentation.Composition;

/// <summary>
/// Immutable AppHost-owned service graph used only while composing pages; views receive narrower
/// page-specific dependency records and never receive this context.
/// </summary>
internal sealed record PageCompositionContext(
    AppSettingsService Settings,
    LocalizationService Localization,
    ClashDataPackageService DataPackages,
    LogStorageService LogStorage,
    ProfileCatalogService Profiles,
    MihomoConnectionService MihomoConnections,
    MainlandChinaTextDisplayService MainlandChinaTextDisplay,
    MihomoControllerClient MihomoController,
    MihomoCoreService MihomoCore,
    CoreConfigurationService CoreConfiguration,
    MihomoServiceManager MihomoService,
    ProxyLatencyService ProxyLatency,
    ProxyNodeCatalogService ProxyNodes,
    RuleCatalogService Rules,
    ApplicationActionService ApplicationActions,
    ApplicationLifecycleService ApplicationLifecycle,
    StartupConflictDetectionService StartupConflicts,
    StartupRestoreFallbackService StartupRestoreFallback,
    WindowsProxyService WindowsProxy,
    WindowsNetworkDiagnosticService WindowsDiagnostics,
    NotificationService Notifications,
    RestartRequiredStateService RestartState,
    RuntimeTrafficRateService RuntimeTraffic,
    TrayStatusService TrayStatus,
    SettingsRuntimeMutationAdapter SettingsRuntimeMutations,
    TriggerPresentationFactory TriggerPresentation,
    StartupGuideComposition StartupGuide,
    IApplicationErrorSink ErrorSink);
