using System;
using ClashSharp.ApplicationModel.Hosting;
using ClashSharp.ApplicationModel.Startup;
using ClashSharp.Hosting.Startup;
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
            services.AddSingleton<IApplicationStartupCoordinator, StartupCoordinator>();
            services.AddSingleton<IStartupStep, ConfigureLocalizationStartupStep>();
            services.AddSingleton<IStartupStep, StartupRestoreFallbackStep>();
            services.AddSingleton<IStartupStep, ProxyRecoveryStartupStep>();
            services.AddSingleton<IStartupStep, AppSettingsAuditStartupStep>();
            services.AddSingleton<IStartupStep, TriggerSupervisorStartupStep>();
            services.AddSingleton<IStartupStep, WindowShellStartupStep>();
            services.AddSingleton<IStartupStep, ConnectionSamplingStartupStep>();
        });
    }
}
