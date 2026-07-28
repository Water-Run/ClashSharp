using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Model;
using ClashSharp.Presentation.Adapters;
using ClashSharp.Presentation.Dialogs;
using ClashSharp.Service;
using ClashSharp.ViewModel;

namespace ClashSharp.Presentation.Composition;

/// <summary>Injected dependencies used by the master-control view's platform-only interactions.</summary>
internal sealed record MasterControlPageDependencies(
    MasterControlViewModel ViewModel,
    Func<string, string> GetString,
    IApplicationErrorSink ErrorSink,
    IStartupGuidePresenter StartupGuide,
    Func<Microsoft.UI.Xaml.XamlRoot, CancellationToken, Task> ShowStartupConflicts,
    Func<IReadOnlyList<ProxyNode>> GetProxyNodes,
    Func<IReadOnlyList<ProxyNode>, CancellationToken, Task<IReadOnlyList<ProxyNode>>> TestProxyLatencyAsync);

/// <summary>Legacy composition boundary for the master-control page.</summary>
/// <remarks>
/// This is the only master-page location allowed to adapt process-wide legacy services. The view
/// receives explicit dependencies so a future host-owned page factory can replace this boundary
/// without changing visual code or the view model.
/// </remarks>
internal static class MasterControlPageComposition
{
    /// <summary>Creates one page dependency graph from the current application-owned services.</summary>
    public static MasterControlPageDependencies Create()
    {
        AppSettingsService settings = AppSettingsService.Instance;
        LocalizationService localization = LocalizationService.Instance;
        ApplicationActionService applicationActions = ApplicationActionService.Instance;
        StartupConflictDetectionService conflictDetection = StartupConflictDetectionService.Instance;
        ProxyNodeCatalogService proxyNodes = ProxyNodeCatalogService.Instance;
        ProxyLatencyService proxyLatency = ProxyLatencyService.Instance;
        CoreConfigurationService coreConfiguration = CoreConfigurationService.Instance;
        StartupRestoreFallbackService startupRestoreFallback = StartupRestoreFallbackService.Instance;
        ProfileCatalogService profileCatalog = ProfileCatalogService.Instance;
        LogStorageService logStorage = LogStorageService.Instance;
        MihomoServiceManager mihomoServiceManager = MihomoServiceManager.Instance;
        RuntimeTrafficRateService runtimeTrafficRate = RuntimeTrafficRateService.Instance;
        IApplicationErrorSink errorSink = ApplicationErrorSink.CreateDefault();
        IMasterControlRuntimeSnapshotSource runtimeSnapshotSource = new MasterControlRuntimeSnapshotSource(
            () => settings.ActiveProfileId,
            coreConfiguration.GetState,
            profileId => coreConfiguration.TryReadProfileConfigurationText(
                profileId,
                out string? configurationText)
                    ? configurationText
                    : null,
            profileCatalog,
            logStorage,
            localization.GetString,
            mihomoServiceManager.GetLatestStatus,
            startupRestoreFallback.GetStatus,
            runtimeTrafficRate.GetLatestSnapshot,
            () => TriggerPresentationCompatibilityFactory.RequireActive().GetSummary(),
            GetWorkingSetBytes);
        MasterControlViewModel viewModel = new(
            new MasterControlLocalizationAdapter(localization),
            new MasterControlCoreAdapter(MihomoCoreService.Instance),
            new MasterControlWindowsProxyAdapter(WindowsProxyService.Instance),
            new MasterControlSettingsAdapter(settings),
            new MasterControlTakeoverAdapter(applicationActions),
            new MasterControlLogAdapter(logStorage),
            new MasterInfoTileLayoutService(settings),
            new MasterHeroStatusLayoutService(settings),
            errorSink,
            new MasterControlTrayStatusAdapter(TrayStatusService.Instance),
            new MasterControlRuntimeAdapter(runtimeSnapshotSource),
            new MasterControlActionsAdapter(applicationActions),
            mode => applicationActions.PublishProxyModeAppliedAsync(mode, CancellationToken.None));

        return new MasterControlPageDependencies(
            viewModel,
            localization.GetString,
            errorSink,
            StartupGuideComposition.Create(errorSink),
            async (xamlRoot, cancellationToken) =>
            {
                IReadOnlyList<StartupConflictIssue> issues = await conflictDetection
                    .CheckConflictsAsync(settings.MixedPort, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                await StartupConflictDialogPresenter.ShowAsync(
                    xamlRoot,
                    issues,
                    localization.GetString,
                    errorSink,
                    cancellationToken);
            },
            proxyNodes.GetNodes,
            proxyLatency.TestNodesAsync);
    }

    private static long GetWorkingSetBytes()
    {
        using Process process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }
}
