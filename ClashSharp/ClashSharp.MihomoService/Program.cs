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
builder.Services.AddMihomoServiceRuntime(serviceOptions);

await builder.Build().RunAsync();
