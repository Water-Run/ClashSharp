using ClashSharp.MihomoService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

MihomoServiceOptions serviceOptions = MihomoServiceOptions.Parse(args);
HostApplicationBuilder builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    // Do not retain the deployment authentication token in the generic configuration graph.
    Args = [],
});
builder.Services.AddWindowsService(options => options.ServiceName = "ClashSharpMihomo");
builder.Services.AddSingleton(serviceOptions);
builder.Services.AddSingleton<MihomoServiceLogBuffer>();
builder.Services.AddSingleton<MihomoRuntimeLogBuffer>();
builder.Services.AddSingleton<MihomoGenerationStore>();
builder.Services.AddSingleton<MihomoEffectiveConfigurationMaterializer>();
builder.Services.AddSingleton<IMihomoChildProcessLauncher, WindowsMihomoChildProcessLauncher>();
builder.Services.AddSingleton<IMihomoControllerTransportFactory, MihomoNamedPipeControllerTransportFactory>();
builder.Services.AddSingleton<IMihomoControllerReadinessProbe, MihomoControllerReadinessProbe>();
builder.Services.AddSingleton<MihomoChildSupervisor>();
builder.Services.AddSingleton<MihomoServiceControllerBroker>();
builder.Services.AddSingleton<MihomoServiceCommandProcessor>();
builder.Services.AddSingleton<MihomoServicePipeServer>();
builder.Services.AddHostedService<MihomoWorker>();

await builder.Build().RunAsync();
