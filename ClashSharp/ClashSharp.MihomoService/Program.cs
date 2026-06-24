/*
 * Clash# Mihomo Service Host
 * Runs bundled mihomo under Windows Service Control Manager
 *
 * @author: WaterRun
 * @file: ClashSharp.MihomoService/Program.cs
 * @date: 2026-06-24
 */

using ClashSharp.MihomoService;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);
builder.Services.AddWindowsService(options => options.ServiceName = "Clash# Mihomo Service");
builder.Services.AddSingleton(MihomoServiceOptions.Parse(args));
builder.Services.AddHostedService<MihomoWorker>();

await builder.Build().RunAsync();
