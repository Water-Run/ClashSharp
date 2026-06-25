/*
 * Mihomo Service Manager Factory
 * Wires production dependencies for transparent-proxy Windows service management
 *
 * @author: WaterRun
 * @file: Service/MihomoServiceManagerFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

public sealed partial class MihomoServiceManager
{
    /// <summary>Shared singleton instance.</summary>
    /// <value>A non-null service manager.</value>
    public static MihomoServiceManager Instance { get; } = MihomoServiceManagerFactory.CreateDefault();
}

/// <summary>Creates mihomo service managers with production dependencies.</summary>
internal static class MihomoServiceManagerFactory
{
    /// <summary>Creates the default service manager used by transparent proxy controls.</summary>
    /// <returns>A service manager wired to sc.exe, application paths, settings, and localization resources.</returns>
    public static MihomoServiceManager CreateDefault()
    {
        return new MihomoServiceManager(
            new MihomoServiceCommandRunner(),
            new MihomoServiceDeploymentContext(
                AppSettingsService.Instance,
                CoreConfigurationService.Instance,
                MihomoCoreService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class MihomoServiceDeploymentContext(
    AppSettingsService settings,
    CoreConfigurationService configuration,
    MihomoCoreService core) : IMihomoServiceDeploymentContext
{
    public string? ResolveServiceHostPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Binaries", "Service", "ClashSharp.MihomoService.exe"),
            Path.Combine(AppContext.BaseDirectory, "ClashSharp.MihomoService.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public string MihomoBinaryPath => core.BinaryPath;

    public CoreConfigurationState EnsureTransparentProxyConfiguration()
    {
        return configuration.EnsureConfiguration(settings.CurrentMode, transparentProxyEnabled: true);
    }
}

internal sealed class MihomoServiceCommandRunner : IMihomoServiceCommandRunner
{
    public MihomoServiceCommandResult RunSc(params string[] arguments)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = BuildScStartInfo(useShellExecute: false, verb: null, arguments),
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
            process.WaitForExit(5000);
            return new MihomoServiceCommandResult(process.ExitCode, output);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            return new MihomoServiceCommandResult(-1, exception.Message);
        }
    }

    public async Task<MihomoServiceCommandResult> RunScElevatedAsync(CancellationToken cancellationToken, params string[] arguments)
    {
        try
        {
            using Process process = new()
            {
                StartInfo = BuildScStartInfo(useShellExecute: true, verb: "runas", arguments),
            };
            process.Start();
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return new MihomoServiceCommandResult(process.ExitCode, string.Empty);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or OperationCanceledException)
        {
            return new MihomoServiceCommandResult(-1, exception.Message);
        }
    }

    /// <summary>Builds sc.exe process start info.</summary>
    private static ProcessStartInfo BuildScStartInfo(bool useShellExecute, string? verb, string[] arguments)
    {
        ProcessStartInfo startInfo = new()
        {
            FileName = "sc.exe",
            UseShellExecute = useShellExecute,
            Verb = verb ?? string.Empty,
            CreateNoWindow = !useShellExecute,
            RedirectStandardOutput = !useShellExecute,
            RedirectStandardError = !useShellExecute,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return startInfo;
    }
}
