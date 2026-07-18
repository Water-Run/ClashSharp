using System;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Mutations;
using ClashSharp.ApplicationModel.Network;
using ClashSharp.ApplicationModel.Startup;
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
    public static AppHost Build(Action<Window> attachWindow)
    {
        ArgumentNullException.ThrowIfNull(attachWindow);
        return AppHost.Build(services =>
        {
            services.AddSingleton(attachWindow);
            services.AddSingleton(_ => AppSettingsService.Instance);
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
            services.AddSingleton<INetworkStateCommitter, LegacyNetworkStateCommitter>();
            services.AddSingleton<IMutationRecoveryPlanResolver, NetworkMutationRecoveryPlanResolver>();
            services.AddSingleton<ApplicationMutationCoordinator>();
            services.AddSingleton<IApplicationMutationCoordinator>(provider =>
                provider.GetRequiredService<ApplicationMutationCoordinator>());
            services.AddSingleton<NetworkStateCoordinator>();
            services.AddSingleton<LegacyNetworkIntentSource>();
            services.AddSingleton(provider => new ApplicationActionService(
                provider.GetRequiredService<AppSettingsService>(),
                provider.GetRequiredService<NetworkStateCoordinator>(),
                MihomoConnectionService.Instance,
                NotificationService.Instance,
                TriggerRuntimeEventHub.Instance,
                LogStorageService.Instance.AppendLog,
                LocalizationService.Instance.GetString,
                () => App.MainWindow?.Close()));
            services.AddSingleton<IApplicationActionDispatcher>(provider =>
                provider.GetRequiredService<ApplicationActionService>());
            services.AddSingleton(provider => TriggerService.CreateDefault(
                provider.GetRequiredService<IApplicationActionDispatcher>()));
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
