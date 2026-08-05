using System;
using System.IO;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Hosting.Startup;
using ClashSharp.Infrastructure.Recovery;
using ClashSharp.Infrastructure.Triggers;
using ClashSharp.Service;
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
        InstallerTransactionState installerTransactionState)
    {
        ArgumentNullException.ThrowIfNull(launchRequest);
        ArgumentNullException.ThrowIfNull(completeWindow);
        ArgumentNullException.ThrowIfNull(lifetimeRequests);
        ArgumentNullException.ThrowIfNull(startupDiagnostics);
        bool isStartupRestoreFallback = launchRequest.Arguments.Contains(
            StartupRestoreFallbackService.HelperArgument,
            StringComparison.OrdinalIgnoreCase);
        string triggerRoot = AppDataPathService.ResolveLocalDataDirectory();
        string triggerDatabasePath = Path.Combine(triggerRoot, "Triggers.db");
        string legacyTriggerPath = Path.Combine(triggerRoot, "Triggers.json");
        Guid triggerProcessEpoch = Guid.NewGuid();
        MutationAdmissionBarrier mutationAdmission = new();
        AppSettingsService.Instance.ConfigureMutationAdmission(mutationAdmission);
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
            services.AddSingleton(_ => ClashDataPackageService.Instance);
            services.AddSingleton(_ => AppSettingsAuditLogService.Instance);
            services.AddSingleton(_ => LocalizationService.Instance);
            services.AddSingleton(_ => LogStorageService.Instance);
            services.AddSingleton(_ => ConnectionSamplingService.Instance);
            services.AddSingleton(provider =>
            {
                LateBoundProfileCatalogMutationCoordinator.Instance.Configure(
                    provider.GetRequiredService<MutationAdmissionBarrier>(),
                    provider.GetRequiredService<FairAsyncMutationGate>());
                return ProfileCatalogService.Instance;
            });
            services.AddSingleton(_ => StartupLaunchServiceFactory.CreateDefault());
            services.AddSingleton(_ => MihomoConnectionService.Instance);
            services.AddSingleton(_ => NetworkTakeoverService.Instance);
            services.AddSingleton(_ => WindowsProxyService.Instance);
            services.AddSingleton(_ => MihomoCoreService.Instance);
            services.AddSingleton(_ => CoreConfigurationService.Instance);
            services.AddSingleton(_ => MihomoServiceManager.Instance);
            services.AddSingleton(_ => ProxyRecoveryService.Instance);
            services.AddSingleton(_ => NotificationService.Instance);
            services.AddSingleton<IIdempotentTriggerNotificationSink>(provider =>
                provider.GetRequiredService<NotificationService>());
            services.AddSingleton(_ => TriggerRuntimeEventHub.Instance);
            services.AddSingleton<ITriggerRuntimeEventSource>(provider =>
                provider.GetRequiredService<TriggerRuntimeEventHub>());
            services.AddSingleton<ITriggerRuntimeEventPublisher>(provider =>
                provider.GetRequiredService<TriggerRuntimeEventHub>());
            services.AddSingleton(mutationAdmission);
            services.AddSingleton<FairAsyncMutationGate>();
            services.AddSingleton<MutationDeadlines>(_ => MutationDeadlines.Default);
            services.AddSingleton<IMutationJournalStore>(_ => new FileMutationJournalStore(
                RecoveryRootPolicy.GetDefaultRootPath()));
            services.AddSingleton<INetworkStateAdapter, LegacyNetworkStateAdapter>();
            services.AddSingleton<INetworkStateObserver>(provider =>
                (INetworkStateObserver)provider.GetRequiredService<INetworkStateAdapter>());
            services.AddSingleton<INetworkStateCommitter, LegacyNetworkStateCommitter>();
            services.AddSingleton<IMutationRecoveryPlanResolver, NetworkMutationRecoveryPlanResolver>();
            services.AddSingleton<ApplicationMutationCoordinator>();
            services.AddSingleton<IApplicationMutationCoordinator>(provider =>
                provider.GetRequiredService<ApplicationMutationCoordinator>());
            services.AddSingleton<NetworkStateCoordinator>();
            services.AddSingleton<IRuntimeShutdownNetworkCoordinator>(provider =>
                provider.GetRequiredService<NetworkStateCoordinator>());
            services.AddSingleton<LegacyNetworkIntentSource>();
            services.AddSingleton(provider => new ApplicationLifecycleService(
                lifetimeRequests,
                installAsPrimaryInstance: true));
            services.AddSingleton(provider => new ApplicationActionService(
                provider.GetRequiredService<AppSettingsService>(),
                provider.GetRequiredService<MutationAdmissionBarrier>(),
                provider.GetRequiredService<NetworkStateCoordinator>(),
                provider.GetRequiredService<ConnectionSamplingService>(),
                MihomoConnectionService.Instance,
                NotificationService.Instance,
                TriggerRuntimeEventHub.Instance,
                LogStorageService.Instance.AppendLog,
                LocalizationService.Instance.GetString,
                provider.GetRequiredService<ApplicationLifecycleService>(),
                provider.GetRequiredService<IApplicationShutdownCoordinator>(),
                provider.GetRequiredService<StartupLaunchService>()));
            services.AddSingleton<IApplicationActionDispatcher>(provider =>
                provider.GetRequiredService<ApplicationActionService>());
            services.AddSingleton(_ => new SqliteTriggerRepository(triggerDatabasePath));
            services.AddSingleton<ITriggerRepository>(provider =>
                provider.GetRequiredService<SqliteTriggerRepository>());
            services.AddSingleton<TriggerDefinitionStore>();
            services.AddSingleton<ITriggerDefinitionStore>(provider =>
                provider.GetRequiredService<TriggerDefinitionStore>());
            services.AddSingleton(provider =>
            {
                AppSettingsService settings = provider.GetRequiredService<AppSettingsService>();
                NotificationService notifications =
                    provider.GetRequiredService<NotificationService>();
                return new TriggerFiredNotificationAdapter(
                    () => settings.TriggerNotificationsEnabled,
                    provider.GetRequiredService<ITriggerDefinitionStore>(),
                    notifications.DeliverTriggerFiredNotificationAsync,
                    notifications.ReportTriggerFiredNotificationFailure);
            });
            services.AddSingleton<ITriggerFiredNotificationSink>(provider =>
                provider.GetRequiredService<TriggerFiredNotificationAdapter>());
            services.AddSingleton(provider => new TriggerMigrationCoordinator(
                provider.GetRequiredService<SqliteTriggerRepository>(),
                legacyTriggerPath,
                provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<ITriggerContextProvider>(provider =>
            {
                TimeProvider timeProvider = provider.GetRequiredService<TimeProvider>();
                return new TriggerContextProviderAdapter(
                    new SqliteTriggerTrafficContextSource(LogStorageService.Instance.DatabasePath),
                    new RuntimeTriggerContextSource(RuntimeTrafficRateService.Instance),
                    timeProvider,
                    timeProvider.GetUtcNow());
            });
            services.AddSingleton<TriggerEvaluator>();
            services.AddSingleton<TriggerExecutionGate>();
            services.AddSingleton(provider => new TriggerLifecycleHandoffCoordinator(
                provider.GetRequiredService<ITriggerRepository>(),
                lifetimeRequests,
                provider.GetRequiredService<TimeProvider>(),
                triggerProcessEpoch));
            services.AddSingleton<ITriggerLifecycleHandoff>(provider =>
                provider.GetRequiredService<TriggerLifecycleHandoffCoordinator>());
            services.AddSingleton<TriggerActionRuntimeAdapter>();
            services.AddSingleton<ITriggerActionRuntime>(provider =>
                provider.GetRequiredService<TriggerActionRuntimeAdapter>());
            services.AddSingleton<TriggerActionExecutor>();
            services.AddSingleton<ITriggerExecutionDispatcher>(provider =>
                provider.GetRequiredService<TriggerActionExecutor>());
            services.AddSingleton(provider => new TriggerExecutionCoordinator(
                provider.GetRequiredService<ITriggerRepository>(),
                provider.GetRequiredService<TriggerExecutionGate>(),
                provider.GetRequiredService<TriggerEvaluator>(),
                provider.GetRequiredService<MutationAdmissionBarrier>(),
                provider.GetRequiredService<ITriggerExecutionDispatcher>(),
                provider.GetRequiredService<TimeProvider>(),
                triggerProcessEpoch));
            services.AddSingleton<TriggerActionReconciler>();
            services.AddSingleton<ITriggerSchedulerEvaluator, TriggerSchedulerEvaluator>();
            services.AddSingleton<TriggerSchedulerSettingsAdapter>();
            services.AddSingleton<ITriggerSchedulerSettings>(provider =>
                provider.GetRequiredService<TriggerSchedulerSettingsAdapter>());
            services.AddSingleton<TriggerSchedulerEventSourceAdapter>();
            services.AddSingleton<ITriggerSchedulerEventSource>(provider =>
                provider.GetRequiredService<TriggerSchedulerEventSourceAdapter>());
            services.AddSingleton<ITriggerSchedulerClock>(provider =>
                new SystemTriggerSchedulerClock(
                    provider.GetRequiredService<TimeProvider>(),
                    TimeSpan.FromSeconds(30)));
            services.AddSingleton<TriggerSchedulerHealthLogAdapter>();
            services.AddSingleton(provider => new TriggerScheduler(
                provider.GetRequiredService<ITriggerSchedulerSettings>(),
                provider.GetRequiredService<ITriggerSchedulerEventSource>(),
                provider.GetRequiredService<ITriggerSchedulerClock>(),
                provider.GetRequiredService<ITriggerSchedulerEvaluator>(),
                provider.GetRequiredService<ITriggerLifecycleHandoff>(),
                provider.GetRequiredService<TriggerSchedulerHealthLogAdapter>().Report));
            services.AddSingleton<TriggerStartupInitializer>();
            services.AddSingleton<ITriggerStartupInitializer>(provider =>
                provider.GetRequiredService<TriggerStartupInitializer>());
            services.AddSingleton<TriggerPresentationCompatibilityFactory>();
            services.AddSingleton<IRuntimeParticipant>(provider =>
                provider.GetRequiredService<TriggerScheduler>());
            services.AddSingleton<IRuntimeParticipant>(provider =>
                provider.GetRequiredService<ConnectionSamplingService>());
            services.AddSingleton<IProfileSubscriptionSchedulerCatalog>(provider =>
                new ProfileSubscriptionSchedulerCatalogAdapter(
                    provider.GetRequiredService<ProfileCatalogService>()));
            services.AddSingleton(provider => new ProfileSubscriptionScheduler(
                provider.GetRequiredService<IProfileSubscriptionSchedulerCatalog>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<LogStorageService>().AppendLog));
            services.AddSingleton<IRuntimeParticipant>(provider =>
                provider.GetRequiredService<ProfileSubscriptionScheduler>());
            services.AddSingleton(provider =>
            {
                LegacyNetworkIntentSource intents =
                    provider.GetRequiredService<LegacyNetworkIntentSource>();
                Func<NetworkIntent> shutdownIntentFactory = isStartupRestoreFallback
                    ? intents.CreateStartupRestoreFallbackShutdown
                    : intents.CreateShutdown;
                return new RuntimeLifecycleCoordinator(
                    provider.GetRequiredService<MutationAdmissionBarrier>(),
                    provider.GetRequiredService<IRuntimeShutdownNetworkCoordinator>(),
                    shutdownIntentFactory,
                    provider.GetServices<IRuntimeParticipant>());
            });
            services.AddSingleton<IApplicationShutdownCoordinator>(provider =>
                provider.GetRequiredService<RuntimeLifecycleCoordinator>());
            services.AddSingleton(provider => TrayCommandServiceFactory.CreateDefault(
                provider.GetRequiredService<ApplicationActionService>()));
            services.AddSingleton(_ => StartupConflictDetectionService.Instance);
            services.AddSingleton<StartupConflictSnapshot>();
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddSingleton<IStartupStep, DataPackageRecoveryStartupStep>();
            services.AddSingleton<IStartupStep, ConfigureLocalizationStartupStep>();
            services.AddSingleton<IStartupStep>(provider =>
                new InstallerTransactionStartupGate(
                    installerTransactionState,
                    provider.GetRequiredService<MutationAdmissionBarrier>()));
            services.AddSingleton<IStartupStep, MutationRecoveryStartupStep>();
            services.AddSingleton<IStartupStep, StartupRestoreFallbackStep>();
            services.AddSingleton<IStartupStep, ProxyRecoveryStartupStep>();
            services.AddSingleton<IStartupStep, AppSettingsAuditStartupStep>();
            services.AddSingleton<IStartupStep, StartupConflictProbeStep>();
            services.AddSingleton<IStartupStep, StartupNetworkBehaviorStep>();
            services.AddSingleton<IStartupStep, TriggerSupervisorStartupStep>();
            services.AddSingleton<IStartupStep, TriggerPresentationStartupStep>();
            services.AddSingleton<IStartupStep, WindowShellStartupStep>();
            services.AddSingleton<IStartupStep, ConnectionSamplingStartupStep>();
            services.AddSingleton<IStartupStep, ProfileSubscriptionSchedulerStartupStep>();
        });
    }
}
