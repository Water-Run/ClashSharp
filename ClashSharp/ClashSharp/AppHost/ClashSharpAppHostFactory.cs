using System;
using System.IO;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Presentation;
using ClashSharp.ApplicationModel.Security;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Hosting.Data;
using ClashSharp.Hosting.Settings;
using ClashSharp.Hosting.Startup;
using ClashSharp.Infrastructure.Data;
using ClashSharp.Infrastructure.Recovery;
using ClashSharp.Infrastructure.Security;
using ClashSharp.Infrastructure.Settings;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Presentation.Composition;
using ClashSharp.Presentation.Navigation;
using ClashSharp.Service;
using ClashSharp.Settings;
using Microsoft.Extensions.DependencyInjection;

namespace ClashSharp.Hosting;

/// <summary>Registers ClashSharp startup services without resolving them.</summary>
internal static class ClashSharpAppHostFactory
{
    public static AppHost Build(
        AppLaunchRequest launchRequest,
        Action<MainWindowStartupContext> completeWindow,
        IApplicationLifetimeRequestSink lifetimeRequests,
        IStartupDiagnosticSink startupDiagnostics,
        InstallerTransactionState installerTransactionState,
        AppGenerationUi? generationUi = null)
    {
        ArgumentNullException.ThrowIfNull(launchRequest);
        ArgumentNullException.ThrowIfNull(completeWindow);
        ArgumentNullException.ThrowIfNull(lifetimeRequests);
        ArgumentNullException.ThrowIfNull(startupDiagnostics);
        bool isStartupRestoreFallback = launchRequest.Arguments.Contains(
            StartupRestoreFallbackService.HelperArgument,
            StringComparison.OrdinalIgnoreCase);
        Lazy<string> dataRoot = new(AppDataPathService.ResolveLocalDataDirectory);
        Guid triggerProcessEpoch = Guid.NewGuid();
        MutationAdmissionBarrier mutationAdmission = new();
        return AppHost.Build(services =>
        {
            services.AddSingleton(completeWindow);
            services.AddSingleton(lifetimeRequests);
            services.AddSingleton(startupDiagnostics);
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton(_ =>
            {
                AppSettingsService settings = AppSettingsService.Instance;
                settings.ConfigureMutationAdmission(mutationAdmission);
                return settings;
            });
            services.AddSingleton(provider =>
            {
                // Legacy recovery can write settings before the localization step resolves them.
                _ = provider.GetRequiredService<AppSettingsService>();
                return ClashDataPackageService.Instance;
            });
            services.AddSingleton(provider => new AppSettingsAuditLogService(
                provider.GetRequiredService<AppSettingsService>(), provider.GetRequiredService<ILogStorage>()));
            services.AddSingleton(_ => LocalizationService.Instance);
            services.AddSingleton(_ => LogStorageService.Instance);
            services.AddSingleton<ILogStorage, GenerationLogStorage>();
            services.AddSingleton<IApplicationErrorSink>(provider => new ApplicationErrorSink(
                provider.GetRequiredService<ILogStorage>().AppendLog,
                provider.GetRequiredService<LocalizationService>().GetString));
            services.AddSingleton<RuntimeLifetimeRegistry>();
            services.AddSingleton<GenerationSamplingRuntime>();
            services.AddSingleton<IConnectionSamplingRuntime>(provider => provider.GetRequiredService<GenerationSamplingRuntime>());
            services.AddSingleton<IProfileCatalog, GenerationProfileCatalog>();
            services.AddSingleton(_ => StartupLaunchServiceFactory.CreateDefault());
            services.AddSingleton(_ => MihomoConnectionService.Instance);
            services.AddSingleton(_ => MihomoControllerClient.Instance);
            services.AddSingleton<IProxySelectionService>(_ => RuntimeDataServices.ProxySelections);
            services.AddSingleton(_ => NetworkTakeoverService.Instance);
            services.AddSingleton(_ => WindowsProxyService.Instance);
            services.AddSingleton(_ => WindowsNetworkDiagnosticService.Instance);
            services.AddSingleton(_ => MihomoCoreService.Instance);
            services.AddSingleton<ICoreConfigurationStore>(_ => RuntimeDataServices.Configuration);
            services.AddSingleton(_ => MihomoServiceManager.Instance);
            services.AddSingleton(_ => ProxyRecoveryService.Instance);
            services.AddSingleton(_ => NotificationService.Instance);
            services.AddSingleton(_ => MainlandChinaTextDisplayService.Instance);
            services.AddSingleton(_ => ProxyLatencyService.Instance);
            services.AddSingleton(_ => ProxyNodeCatalogService.Instance);
            services.AddSingleton(_ => RuleCatalogService.Instance);
            services.AddSingleton(_ => RestartRequiredStateService.Instance);
            services.AddSingleton(_ => RuntimeTrafficRateService.Instance);
            services.AddSingleton(_ => StartupRestoreFallbackService.Instance);
            services.AddSingleton(_ => TrayStatusService.Instance);
            services.AddSingleton<IIdempotentTriggerNotificationSink>(provider =>
                provider.GetRequiredService<NotificationService>());
            services.AddSingleton(_ => TriggerRuntimeEventHub.Instance);
            services.AddSingleton<ITriggerRuntimeEventSource>(provider =>
                provider.GetRequiredService<TriggerRuntimeEventHub>());
            services.AddSingleton<ITriggerRuntimeEventPublisher>(provider =>
                provider.GetRequiredService<TriggerRuntimeEventHub>());
            services.AddSingleton(mutationAdmission);
            services.AddSingleton<DataGenerationManager>();
            services.AddSingleton<RuntimeDataBinding>();
            services.AddSingleton<GenerationSettingsAuthority>();
            services.AddSingleton<ISettingsAuthority>(provider => provider.GetRequiredService<GenerationSettingsAuthority>());
            services.AddSingleton<IRuntimeSettingsAuthority>(provider => provider.GetRequiredService<GenerationSettingsAuthority>());
            services.AddSingleton<ISettingsRuntimeGroupReset>(provider => provider.GetRequiredService<GenerationSettingsAuthority>());
            services.AddSingleton<ISettingsApplicationRecovery>(provider => provider.GetRequiredService<GenerationSettingsAuthority>());
            services.AddSingleton<IControllerCredentialStore, WindowsControllerCredentialStore>();
            services.AddSingleton<ControllerCredentialService>();
            services.AddSingleton(_ => MihomoControllerCredentials.Instance);
            services.AddSingleton<IControllerCredentialProvider>(provider => provider.GetRequiredService<MihomoControllerCredentials>());
            services.AddSingleton<FairAsyncMutationGate>();
            services.AddSingleton<MutationDeadlines>(_ => MutationDeadlines.Default);
            services.AddSingleton<IMutationJournalStore>(_ => new FileMutationJournalStore(
                RecoveryRootPolicy.GetDefaultRootPath()));
            services.AddSingleton<INetworkStateAdapter, LegacyNetworkStateAdapter>();
            services.AddSingleton<INetworkStateObserver>(provider => new GenerationNetworkStateObserver(
                provider.GetRequiredService<DataGenerationManager>(),
                (INetworkStateObserver)provider.GetRequiredService<INetworkStateAdapter>()));
            services.AddSingleton<INetworkStateCommitter, LegacyNetworkStateCommitter>();
            services.AddSingleton<IMutationRecoveryPlanResolver, NetworkMutationRecoveryPlanResolver>();
            services.AddSingleton<ApplicationMutationCoordinator>();
            services.AddSingleton<IApplicationMutationCoordinator>(provider =>
                provider.GetRequiredService<ApplicationMutationCoordinator>());
            services.AddSingleton<NetworkStateCoordinator>();
            services.AddSingleton<NetworkMaintenanceCoordinator>();
            services.AddSingleton<IRuntimeShutdownNetworkCoordinator>(provider =>
                provider.GetRequiredService<NetworkStateCoordinator>());
            services.AddSingleton<LegacyNetworkIntentSource>();
            services.AddSingleton(provider => new ApplicationLifecycleService(
                lifetimeRequests,
                installAsPrimaryInstance: true));
            services.AddSingleton<SettingsExportCoordinator>();
            services.AddSingleton<IApplicationDataClearOperationFactory>(provider => new GenerationDataClearOperationFactory(
                provider.GetRequiredService<DataGenerationManager>(), mutationAdmission,
                provider.GetRequiredService<ControllerCredentialService>(), provider.GetRequiredService<AppSettingsService>(),
                provider.GetRequiredService<RuntimeLifecycleCoordinator>(), dataRoot.Value));
            services.AddSingleton(provider => new ApplicationActionService(
                provider.GetRequiredService<IRuntimeSettingsAuthority>(),
                provider.GetRequiredService<MutationAdmissionBarrier>(),
                provider.GetRequiredService<NetworkStateCoordinator>(),
                provider.GetRequiredService<INetworkStateObserver>(),
                provider.GetRequiredService<IConnectionSamplingRuntime>(),
                provider.GetRequiredService<MihomoConnectionService>(),
                provider.GetRequiredService<NotificationService>(),
                provider.GetRequiredService<TriggerRuntimeEventHub>(),
                provider.GetRequiredService<ILogStorage>().AppendLog,
                provider.GetRequiredService<LocalizationService>().GetString,
                provider.GetRequiredService<ApplicationLifecycleService>(),
                provider.GetRequiredService<RuntimeLifecycleCoordinator>(),
                provider.GetRequiredService<StartupLaunchService>(),
                provider.GetRequiredService<IControllerCredentialProvider>(),
                dataClearOperations: provider.GetRequiredService<IApplicationDataClearOperationFactory>()));
            services.AddSingleton<IApplicationActionDispatcher>(provider =>
                provider.GetRequiredService<ApplicationActionService>());
            services.AddSingleton<SettingsRuntimeMutationAdapter>();
            services.AddSingleton<INetworkSettingsRuntime, GenerationNetworkSettingsRuntime>();
            services.AddSingleton<ITriggerDefinitionStore, GenerationTriggerDefinitionStore>();
            services.AddSingleton<TriggerPresentationFactory>();
            services.AddSingleton<IDataGenerationStore>(_ => new FileDataGenerationStore(dataRoot.Value));
            services.AddSingleton<IDataPackageExporter>(provider => new GenerationDataPackageExporter(
                provider.GetRequiredService<DataGenerationManager>(), mutationAdmission, dataRoot.Value));
            services.AddSingleton<IDataGenerationBootstrapFactory>(provider =>
            {
                AppGenerationUi ui = generationUi ?? throw new InvalidOperationException("The generation startup window is unavailable.");
                var authority = provider.GetRequiredService<GenerationSettingsAuthority>();
                var takeover = provider.GetRequiredService<NetworkTakeoverService>();
                var localization = provider.GetRequiredService<LocalizationService>();
                AppDataGenerationRuntimeComposer runtime = new(mutationAdmission, provider.GetRequiredService<DataGenerationManager>(), authority, ui.CreateDispatcher,
                    ui.CreateAppearance(localization), provider.GetRequiredService<StartupLaunchService>(),
                    provider.GetRequiredService<MihomoConnectionService>(), provider.GetRequiredService<RuntimeTrafficRateService>(),
                    takeover, provider.GetRequiredService<WindowsProxyService>(), provider.GetRequiredService<MihomoServiceManager>(),
                    provider.GetRequiredService<NotificationService>(), provider.GetRequiredService<ITriggerRuntimeEventSource>(),
                    lifetimeRequests, ui.ExitRequested, provider.GetRequiredService<TimeProvider>(), triggerProcessEpoch, localization.GetString);
                return new AppDataGenerationFactory(dataRoot.Value, mutationAdmission, new WindowsLegacySettingsSource(SettingsRegistry.Default),
                    async (lease, token) =>
                    {
                        await provider.GetRequiredService<ClashDataPackageService>().ReconcilePendingTransactionAdmittedAsync(lease, token).ConfigureAwait(false);
                        // Generated runtime files are intentionally not migrated. Release the
                        // old root's verified native ownership before opening a new runtime root,
                        // while preserving the desired mode for startup reconciliation.
                        AppSettingsService legacy = provider.GetRequiredService<AppSettingsService>();
                        var stopped = await provider.GetRequiredService<NetworkStateCoordinator>().ApplyShutdownAsync(
                            NetworkIntent.Shutdown(ClashSharp.Model.ClashSharpMode.Disabled, false, legacy.MixedPort), lease, token).ConfigureAwait(false);
                        if (stopped.Outcome != MutationOutcome.Succeeded)
                        {
                            throw new NetworkTransitionFailedException(stopped.Outcome, stopped.ErrorCode);
                        }
                    },
                    session => new AppDataGenerationRepositories(session, authority, mutationAdmission,
                        provider.GetRequiredService<FairAsyncMutationGate>(), provider.GetRequiredService<IControllerCredentialProvider>(),
                        new CoreConfigurationProfileMetricsAdapter(), new CoreConfigurationValidator(), localization.GetString,
                        (configuration, ownedSession) => new GenerationProfileRuntime(ownedSession, configuration),
                        configuration => new ProxySelectionService(
                            new ProxySelectionStore(Path.Combine(session.Generation.RootPath, "mihomo", "proxy-selections.json")),
                            provider.GetRequiredService<MihomoControllerClient>().GetProxyGroupsAsync,
                            provider.GetRequiredService<MihomoControllerClient>().SelectProxyAsync,
                            configuration.ObserveRuntimeConfigurationIntegrity,
                            new ProfileCatalogMutationCoordinator(mutationAdmission, provider.GetRequiredService<FairAsyncMutationGate>()))), runtime.ComposeAsync);
            });
            services.AddSingleton<DataGenerationBootstrapper>();
            services.AddSingleton(provider => new GenerationDataCandidatePreparer(dataRoot.Value, mutationAdmission,
                provider.GetRequiredService<IDataGenerationBootstrapFactory>()));
            services.AddSingleton<GenerationReplacementCoordinator>();
            services.AddSingleton<ISettingsDataReplacement>(provider => new GenerationSettingsDataReplacement(
                provider.GetRequiredService<GenerationReplacementCoordinator>(),
                provider.GetRequiredService<RestartRequiredStateService>().RequireRestart,
                reason => provider.GetRequiredService<ApplicationLifecycleService>().RequestRestart(reason)));
            services.AddSingleton<IGenerationReplacementJournal>(_ => new FileGenerationReplacementJournal(dataRoot.Value));
            services.AddSingleton<GenerationReplacementStartupRecovery>();
            services.AddSingleton(provider => new RuntimeLifecycleCoordinator(
                    provider.GetRequiredService<MutationAdmissionBarrier>(),
                    provider.GetRequiredService<RuntimeLifetimeRegistry>(),
                    networkPolicy: isStartupRestoreFallback
                        ? RuntimeShutdownNetworkPolicy.PreserveCurrentState
                        : RuntimeShutdownNetworkPolicy.ApplyConfiguredIntent));
            services.AddSingleton<IApplicationShutdownCoordinator>(provider =>
                provider.GetRequiredService<RuntimeLifecycleCoordinator>());
            services.AddSingleton(provider => TrayCommandServiceFactory.CreateDefault(
                provider.GetRequiredService<ApplicationActionService>()));
            services.AddSingleton(_ => StartupConflictDetectionService.Instance);
            services.AddSingleton<StartupGuideComposition>();
            services.AddSingleton<PageCompositionContext>();
            services.AddSingleton<ShellNavigationService>();
            services.AddSingleton<IShellNavigationService>(provider =>
                provider.GetRequiredService<ShellNavigationService>());
            services.AddSingleton<IPageFactory, ApplicationPageFactory>();
            services.AddSingleton<MainWindowComposition.Runtime>();
            services.AddSingleton<StartupConflictSnapshot>();
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddDeferredStartupStep<RuntimeDataBindingStartupStep>("runtime-data-binding", 25);
            services.AddDeferredStartupStep<DataPackageRecoveryStartupStep>("data-package-recovery", 50);
            services.AddDeferredStartupStep<ConfigureLocalizationStartupStep>("configure-localization", 100);
            services.AddSingleton(provider =>
                new InstallerTransactionStartupGate(
                    installerTransactionState,
                    provider.GetRequiredService<MutationAdmissionBarrier>()));
            services.AddDeferredStartupStep<InstallerTransactionStartupGate>("installer-transaction-gate", 125);
            services.AddDeferredStartupStep<ControllerCredentialStartupStep>("controller-credential", 140);
            services.AddDeferredStartupStep<GenerationRecoveryDataStartupStep>("recovery-data-generation", 142);
            services.AddDeferredStartupStep<RuntimeShutdownOwnershipStartupStep>("runtime-shutdown-ownership", 145);
            services.AddDeferredStartupStep<MutationRecoveryStartupStep>("mutation-recovery", 150);
            services.AddDeferredStartupStep<StartupRestoreFallbackStep>("startup-restore-fallback", 200);
            services.AddDeferredStartupStep<DataGenerationStartupStep>("data-generation", 225);
            services.AddDeferredStartupStep<ProxyRecoveryStartupStep>("proxy-recovery", 300);
            services.AddDeferredStartupStep<AppSettingsAuditStartupStep>("settings-audit", 400);
            services.AddDeferredStartupStep<StartupConflictProbeStep>("startup-conflict-probe", 425);
            services.AddSingleton(provider => new GenerationRuntimeActivationStartupStep(
                provider.GetRequiredService<DataGenerationStartupStep>(),
                provider.GetRequiredService<StartupConflictSnapshot>(),
                provider.GetRequiredService<ApplicationActionService>().PublishProxyModeAppliedAsync));
            services.AddDeferredStartupStep<GenerationRuntimeActivationStartupStep>("startup-network-behavior", 450);

            services.AddDeferredStartupStep<WindowShellStartupStep>("window-shell", 600);
            services.AddDeferredStartupStep<ConnectionSamplingStartupStep>("connection-sampling", 700);
            services.AddDeferredStartupStep<ProfileSubscriptionSchedulerStartupStep>("profile-subscription-updates", 710);
        });
    }
}
