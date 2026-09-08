using Microsoft.Extensions.DependencyInjection;

namespace ClashSharp.MihomoService;

/// <summary>Composes the service's internal implementations without reflection-based constructor selection.</summary>
internal static class MihomoServiceComposition
{
    /// <summary>Registers the production runtime without creating directories, pipes, or child processes.</summary>
    internal static IServiceCollection AddMihomoServiceRuntime(
        this IServiceCollection services,
        MihomoServiceOptions options)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(options);
        services.AddSingleton(options);
        services.AddSingleton(static provider => new MihomoServiceLogBuffer(
            provider.GetRequiredService<MihomoServiceOptions>()));
        services.AddSingleton(static provider => new MihomoRuntimeLogBuffer(
            provider.GetRequiredService<MihomoServiceLogBuffer>()));
        services.AddSingleton(static provider => new MihomoGenerationStore(
            provider.GetRequiredService<MihomoServiceOptions>()));
        services.AddSingleton(static provider => new MihomoEffectiveConfigurationMaterializer(
            provider.GetRequiredService<MihomoServiceOptions>()));
        services.AddSingleton<IMihomoChildProcessLauncher>(static _ => new WindowsMihomoChildProcessLauncher());
        services.AddSingleton<IMihomoControllerTransportFactory>(static _ => new MihomoNamedPipeControllerTransportFactory());
        services.AddSingleton<IMihomoControllerReadinessProbe>(static provider => new MihomoControllerReadinessProbe(
            provider.GetRequiredService<IMihomoControllerTransportFactory>()));
        services.AddSingleton(static provider => new MihomoChildSupervisor(
            provider.GetRequiredService<MihomoServiceOptions>(),
            provider.GetRequiredService<MihomoGenerationStore>(),
            provider.GetRequiredService<MihomoEffectiveConfigurationMaterializer>(),
            provider.GetRequiredService<IMihomoChildProcessLauncher>(),
            provider.GetRequiredService<IMihomoControllerReadinessProbe>(),
            provider.GetRequiredService<MihomoServiceLogBuffer>(),
            provider.GetRequiredService<MihomoRuntimeLogBuffer>()));
        services.AddSingleton(static provider => new MihomoServiceControllerBroker(
            provider.GetRequiredService<MihomoChildSupervisor>(),
            provider.GetRequiredService<IMihomoControllerTransportFactory>(),
            provider.GetRequiredService<MihomoRuntimeLogBuffer>(),
            provider.GetRequiredService<MihomoServiceLogBuffer>()));
        services.AddSingleton(static provider => new MihomoServiceCommandProcessor(
            provider.GetRequiredService<MihomoServiceOptions>(),
            provider.GetRequiredService<MihomoChildSupervisor>(),
            provider.GetRequiredService<MihomoServiceLogBuffer>(),
            provider.GetRequiredService<MihomoServiceControllerBroker>()));
        services.AddSingleton(static provider => new MihomoServicePipeServer(
            provider.GetRequiredService<MihomoServiceOptions>(),
            provider.GetRequiredService<MihomoServiceCommandProcessor>(),
            provider.GetRequiredService<MihomoServiceLogBuffer>()));
        services.AddHostedService<MihomoWorker>();
        return services;
    }
}
