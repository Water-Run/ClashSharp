/*
 * Windows Network Diagnostic Service Factory
 * Wires production dependencies for Windows-native diagnostics
 *
 * @author: WaterRun
 * @file: Service/WindowsNetworkDiagnosticServiceFactory.cs
 * @date: 2026-06-25
 */

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Service;

public sealed partial class WindowsNetworkDiagnosticService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="WindowsNetworkDiagnosticService"/> instance.</value>
    public static WindowsNetworkDiagnosticService Instance { get; } = WindowsNetworkDiagnosticServiceFactory.CreateDefault();
}

/// <summary>Creates Windows network diagnostic service instances with production dependencies.</summary>
internal static class WindowsNetworkDiagnosticServiceFactory
{
    /// <summary>Creates the default Windows network diagnostic service.</summary>
    /// <returns>A service wired to persistent settings, user environment variables, process execution, and localization resources.</returns>
    public static WindowsNetworkDiagnosticService CreateDefault()
    {
        return new WindowsNetworkDiagnosticService(
            new WindowsDiagnosticSettingsAdapter(AppSettingsService.Instance),
            new WindowsDiagnosticEnvironmentAdapter(),
            new WindowsDiagnosticProcessRunner(),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class WindowsDiagnosticSettingsAdapter(AppSettingsService settings) : IWindowsDiagnosticSettings
{
    public int MixedPort => settings.MixedPort;
}

internal sealed class WindowsDiagnosticEnvironmentAdapter : IWindowsDiagnosticEnvironment
{
    public string? GetUserEnvironmentVariable(string name)
    {
        return Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);
    }

    public void SetUserEnvironmentVariable(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
    }
}

internal sealed class WindowsDiagnosticProcessRunner : IWindowsDiagnosticProcessRunner
{
    public async Task<WindowsDiagnosticProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        ArgumentNullException.ThrowIfNull(arguments);

        ProcessStartInfo startInfo = new()
        {
            FileName = fileName,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using Process process = new()
        {
            StartInfo = startInfo,
        };

        try
        {
            if (!process.Start())
            {
                return new WindowsDiagnosticProcessResult(-1, string.Empty, "Process could not be started.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new WindowsDiagnosticProcessResult(-1, string.Empty, exception.Message);
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

            return new WindowsDiagnosticProcessResult(
                process.ExitCode,
                await outputTask.ConfigureAwait(false),
                await errorTask.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            TryKillProcess(process);
            throw;
        }
    }

    /// <summary>Attempts to terminate <paramref name="process"/> after timeout or cancellation.</summary>
    /// <param name="process">Process to terminate. Must not be null.</param>
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
