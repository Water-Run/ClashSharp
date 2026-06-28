/*
 * Core Configuration Service Factory
 * Wires production dependencies for mihomo configuration management
 *
 * @author: WaterRun
 * @file: Service/CoreConfigurationServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

public sealed partial class CoreConfigurationService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="CoreConfigurationService"/> instance.</value>
    public static CoreConfigurationService Instance { get; } = CoreConfigurationServiceFactory.CreateDefault();
}

/// <summary>Creates core configuration services with production dependencies.</summary>
internal static class CoreConfigurationServiceFactory
{
    /// <summary>Creates the default service used by runtime and profile catalog flows.</summary>
    public static CoreConfigurationService CreateDefault()
    {
        return new CoreConfigurationService(
            Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "mihomo"),
            new CoreConfigurationSettingsAdapter(AppSettingsService.Instance),
            new CoreConfigurationProfileMetricsAdapter(),
            new CoreConfigurationValidator(),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class CoreConfigurationSettingsAdapter(AppSettingsService settings) : ICoreConfigurationSettings
{
    public bool TransparentProxyEnabled => settings.TransparentProxyEnabled;

    public int MixedPort => settings.MixedPort;

    public string ActiveProfileId => settings.ActiveProfileId;
}

internal sealed class CoreConfigurationProfileMetricsAdapter : ICoreConfigurationProfileMetrics
{
    public int CountNodes(string configurationText)
    {
        return MihomoProfileParserService.Instance.ParseNodes(configurationText).Count;
    }

    public int CountRules(string configurationText)
    {
        return MihomoProfileParserService.Instance.ParseRules(configurationText).Count;
    }
}

internal sealed class CoreConfigurationValidator : ICoreConfigurationValidator
{
    public async Task ValidateAsync(string workingDirectory, string configurationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workingDirectory);
        ArgumentNullException.ThrowIfNull(configurationPath);

        MihomoCoreService coreService = MihomoCoreService.Instance;
        if (!coreService.IsBinaryAvailable)
        {
            return;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = coreService.BinaryPath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        startInfo.ArgumentList.Add("-t");
        startInfo.ArgumentList.Add("-d");
        startInfo.ArgumentList.Add(workingDirectory);
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add(configurationPath);

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        if (!process.Start())
        {
            throw new InvalidOperationException("Unable to start bundled mihomo for configuration validation.");
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(15));

        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            string output = (await outputTask.ConfigureAwait(false)).Trim();
            string error = (await errorTask.ConfigureAwait(false)).Trim();

            if (process.ExitCode != 0)
            {
                string detail = string.IsNullOrWhiteSpace(error) ? output : error;
                throw new InvalidOperationException($"mihomo rejected the imported configuration: {detail}");
            }
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    private static void TryKillProcess(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception) when (IsExpectedProcessTerminationException(exception))
        {
        }
    }

    private static bool IsExpectedProcessTerminationException(Exception exception)
    {
        return exception is InvalidOperationException or Win32Exception or UnauthorizedAccessException;
    }
}
