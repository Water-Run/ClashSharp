using System;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Hosting.Compatibility;
using ClashSharp.Hosting.Startup;
using ClashSharp.Infrastructure.Recovery;
using ClashSharp.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;

namespace ClashSharp.Hosting;

/// <summary>Registers ClashSharp startup services without resolving them.</summary>
internal static class ClashSharpAppHostFactory
{
    public static AppHost Build(
        Action<Window> attachWindow,
        IApplicationLifetimeRequestSink lifetimeRequests)
    {
        ArgumentNullException.ThrowIfNull(attachWindow);
        ArgumentNullException.ThrowIfNull(lifetimeRequests);
        return AppHost.Build(services =>
        {
            services.AddSingleton(attachWindow);
            services.AddSingleton(lifetimeRequests);
            services.AddSingleton(_ => AppSettingsService.Instance);
            services.AddSingleton(_ => ConnectionSamplingService.Instance);
            services.AddSingleton(_ => NetworkTakeoverService.Instance);
            services.AddSingleton(_ => WindowsProxyService.Instance);
            services.AddSingleton(_ => MihomoCoreService.Instance);
            services.AddSingleton(_ => CoreConfigurationService.Instance);
            services.AddSingleton(_ => MihomoServiceManager.Instance);
            services.AddSingleton(_ => ProxyRecoveryService.Instance);
            services.AddSingleton<MutationAdmissionBarrier>();
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
                provider.GetRequiredService<NetworkStateCoordinator>(),
                provider.GetRequiredService<ConnectionSamplingService>(),
                MihomoConnectionService.Instance,
                NotificationService.Instance,
                TriggerRuntimeEventHub.Instance,
                LogStorageService.Instance.AppendLog,
                LocalizationService.Instance.GetString,
                provider.GetRequiredService<ApplicationLifecycleService>()));
            services.AddSingleton<IApplicationActionDispatcher>(provider =>
                provider.GetRequiredService<ApplicationActionService>());
            services.AddSingleton<ITriggerContextProvider>(_ =>
            {
                TimeProvider timeProvider = TimeProvider.System;
                return new TriggerContextProviderAdapter(
                    new SqliteTriggerTrafficContextSource(LogStorageService.Instance.DatabasePath),
                    new RuntimeTriggerContextSource(RuntimeTrafficRateService.Instance),
                    timeProvider,
                    timeProvider.GetUtcNow());
            });
            services.AddSingleton<TriggerEvaluationContextFactory>();
            services.AddSingleton(provider => TriggerService.CreateDefault(
                provider.GetRequiredService<IApplicationActionDispatcher>(),
                provider.GetRequiredService<TriggerEvaluationContextFactory>()));
            services.AddSingleton<LegacyTriggerRuntimeParticipant>();
            services.AddSingleton<IRuntimeParticipant>(provider =>
                provider.GetRequiredService<LegacyTriggerRuntimeParticipant>());
            services.AddSingleton<IRuntimeParticipant>(provider =>
                provider.GetRequiredService<ConnectionSamplingService>());
            services.AddSingleton(provider => new RuntimeLifecycleCoordinator(
                provider.GetRequiredService<MutationAdmissionBarrier>(),
                provider.GetRequiredService<IRuntimeShutdownNetworkCoordinator>(),
                provider.GetRequiredService<LegacyNetworkIntentSource>().CreateShutdown,
                provider.GetServices<IRuntimeParticipant>()));
            services.AddSingleton<IApplicationShutdownCoordinator>(provider =>
                provider.GetRequiredService<RuntimeLifecycleCoordinator>());
            services.AddSingleton(provider => TrayCommandServiceFactory.CreateDefault(
                provider.GetRequiredService<ApplicationActionService>()));
            services.AddSingleton(_ => StartupConflictDetectionService.Instance);
            services.AddSingleton<StartupConflictSnapshot>();
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddSingleton<IStartupStep, ConfigureLocalizationStartupStep>();
            services.AddSingleton<IStartupStep, MutationRecoveryStartupStep>();
            services.AddSingleton<IStartupStep, StartupRestoreFallbackStep>();
            services.AddSingleton<IStartupStep, ProxyRecoveryStartupStep>();
            services.AddSingleton<IStartupStep, AppSettingsAuditStartupStep>();
            services.AddSingleton<IStartupStep, StartupConflictProbeStep>();
            services.AddSingleton<IStartupStep, StartupNetworkBehaviorStep>();
            services.AddSingleton<IStartupStep, TriggerSupervisorStartupStep>();
            services.AddSingleton<IStartupStep, WindowShellStartupStep>();
            services.AddSingleton<IStartupStep, ConnectionSamplingStartupStep>();
        });
    }
}
