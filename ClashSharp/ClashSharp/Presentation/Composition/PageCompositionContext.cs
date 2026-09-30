using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Settings;
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
    IDataPackageExporter DataPackages,
    ILogStorage LogStorage,
    IProfileCatalog Profiles,
    MihomoConnectionService MihomoConnections,
    MainlandChinaTextDisplayService MainlandChinaTextDisplay,
    MihomoControllerClient MihomoController,
    IProxySelectionService ProxySelections,
    MihomoCoreService MihomoCore,
    ICoreConfigurationStore CoreConfiguration,
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
    IApplicationErrorSink ErrorSink,
    SettingsExportCoordinator SettingsExports,
    ISettingsDataReplacement DataReplacement);
