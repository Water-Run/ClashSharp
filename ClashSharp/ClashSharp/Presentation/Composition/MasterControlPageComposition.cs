using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Presentation;
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
    Func<IReadOnlyList<ProxyNode>, CancellationToken, Task<IReadOnlyList<ProxyNode>>> TestProxyLatencyAsync,
    Action OpenSettings);

/// <summary>Builds the explicit dependency graph for the master-control page.</summary>
internal static class MasterControlPageComposition
{
    /// <summary>Creates one page dependency graph from the AppHost-owned page context.</summary>
    public static MasterControlPageDependencies Create(
        PageCompositionContext context,
        Action openSettings)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(openSettings);
        AppSettingsService settings = context.Settings;
        LocalizationService localization = context.Localization;
        ApplicationActionService applicationActions = context.ApplicationActions;
        StartupConflictDetectionService conflictDetection = context.StartupConflicts;
        ProxyNodeCatalogService proxyNodes = context.ProxyNodes;
        ProxyLatencyService proxyLatency = context.ProxyLatency;
        CoreConfigurationService coreConfiguration = context.CoreConfiguration;
        StartupRestoreFallbackService startupRestoreFallback = context.StartupRestoreFallback;
        ProfileCatalogService profileCatalog = context.Profiles;
        LogStorageService logStorage = context.LogStorage;
        MihomoCoreService mihomoCore = context.MihomoCore;
        MihomoServiceManager mihomoServiceManager = context.MihomoService;
        RuntimeTrafficRateService runtimeTrafficRate = context.RuntimeTraffic;
        IApplicationErrorSink errorSink = context.ErrorSink;
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
            context.TriggerPresentation.GetSummary,
            GetWorkingSetBytes,
            () => mihomoCore.IsRunning && !mihomoCore.HasOwnershipFault,
            () => settings.TransparentProxyEnabled
                && settings.CurrentMode is ClashSharpMode.RuleTakeover or ClashSharpMode.FullTakeover,
            coreConfiguration.ObserveRuntimeConfigurationIntegrity);
        MasterControlViewModel viewModel = new(
            new MasterControlLocalizationAdapter(localization),
            new MasterControlCoreAdapter(mihomoCore),
            new MasterControlWindowsProxyAdapter(context.WindowsProxy),
            new MasterControlSettingsAdapter(settings),
            new MasterControlTakeoverAdapter(applicationActions),
            new MasterControlLogAdapter(logStorage),
            new MasterInfoTileLayoutService(settings),
            new MasterHeroStatusLayoutService(settings),
            errorSink,
            new MasterControlTrayStatusAdapter(context.TrayStatus),
            new MasterControlRuntimeAdapter(runtimeSnapshotSource),
            new MasterControlActionsAdapter(applicationActions),
            mode => applicationActions.PublishProxyModeAppliedAsync(mode, CancellationToken.None));

        return new MasterControlPageDependencies(
            viewModel,
            localization.GetString,
            errorSink,
            context.StartupGuide.Create(errorSink),
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
            proxyLatency.TestNodesAsync,
            openSettings);
    }

    private static long GetWorkingSetBytes()
    {
        using Process process = Process.GetCurrentProcess();
        return process.WorkingSet64;
    }
}
