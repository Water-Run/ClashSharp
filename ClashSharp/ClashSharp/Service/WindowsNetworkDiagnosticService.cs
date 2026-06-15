/*
 * Windows Network Diagnostic Service
 * Provides independent WSL, terminal, and Microsoft Store network diagnostics, apply, and reset actions
 *
 * @author: WaterRun
 * @file: Service/WindowsNetworkDiagnosticService.cs
 * @date: 2026-06-15
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides independent WSL, terminal, and Microsoft Store network diagnostics, apply, and reset actions.</summary>
/// <remarks>
/// Invariants: Each target diagnosis reports its own readiness; WSL repair also writes proxy environment variables required by WSLENV bridging.
/// Thread safety: Stateless service; process launches are independent.
/// Side effects: Apply and reset methods may update user environment variables or Microsoft Store loopback exemptions.
/// </remarks>
public sealed class WindowsNetworkDiagnosticService
{
    /// <summary>Shared singleton instance created once at type initialization.</summary>
    /// <value>A non-null <see cref="WindowsNetworkDiagnosticService"/> instance.</value>
    public static WindowsNetworkDiagnosticService Instance { get; } = new();

    /// <summary>Microsoft Store package family name used by CheckNetIsolation.</summary>
    private const string MicrosoftStorePackageFamilyName = "Microsoft.WindowsStore_8wekyb3d8bbwe";

    /// <summary>WSLENV token set used to bridge proxy variables into WSL distributions.</summary>
    private static readonly string[] WslEnvProxyTokens = ["HTTP_PROXY/u", "HTTPS_PROXY/u", "ALL_PROXY/u", "NO_PROXY/u"];

    /// <summary>Loopback hosts excluded from terminal and WSL proxy routing.</summary>
    private const string NoProxyValue = "localhost,127.0.0.1,::1";

    /// <summary>Initializes the Windows network diagnostic service.</summary>
    private WindowsNetworkDiagnosticService()
    {
    }

    /// <summary>Diagnoses one Windows-native network target.</summary>
    /// <param name="target">Diagnostic target.</param>
    /// <param name="cancellationToken">Cancels external process checks.</param>
    /// <returns>Diagnostic result for <paramref name="target"/>.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not supported.</exception>
    public Task<WindowsDiagnosticResult> DiagnoseAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
    {
        return target switch
        {
            WindowsDiagnosticTarget.Wsl => DiagnoseWslAsync(cancellationToken),
            WindowsDiagnosticTarget.Terminal => Task.FromResult(DiagnoseTerminal()),
            WindowsDiagnosticTarget.MicrosoftStore => DiagnoseMicrosoftStoreAsync(cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported Windows diagnostic target."),
        };
    }

    /// <summary>Applies one Windows-native network repair action.</summary>
    /// <param name="target">Diagnostic target to apply.</param>
    /// <param name="cancellationToken">Cancels external process actions.</param>
    /// <returns>Diagnostic result after the apply action.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not supported.</exception>
    public async Task<WindowsDiagnosticResult> ApplyAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case WindowsDiagnosticTarget.Wsl:
                ApplyTerminalProxyEnvironment();
                ApplyWslProxyBridge();
                return await DiagnoseWslAsync(cancellationToken).ConfigureAwait(false);
            case WindowsDiagnosticTarget.Terminal:
                ApplyTerminalProxyEnvironment();
                return DiagnoseTerminal();
            case WindowsDiagnosticTarget.MicrosoftStore:
                await ApplyMicrosoftStoreLoopbackAsync(cancellationToken).ConfigureAwait(false);
                return await DiagnoseMicrosoftStoreAsync(cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported Windows diagnostic target.");
        }
    }

    /// <summary>Resets one Windows-native network repair action.</summary>
    /// <param name="target">Diagnostic target to reset.</param>
    /// <param name="cancellationToken">Cancels external process actions.</param>
    /// <returns>Diagnostic result after the reset action.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not supported.</exception>
    public async Task<WindowsDiagnosticResult> ResetAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
    {
        switch (target)
        {
            case WindowsDiagnosticTarget.Wsl:
                ResetWslProxyBridge();
                return await DiagnoseWslAsync(cancellationToken).ConfigureAwait(false);
            case WindowsDiagnosticTarget.Terminal:
                ResetTerminalProxyEnvironment();
                return DiagnoseTerminal();
            case WindowsDiagnosticTarget.MicrosoftStore:
                await ResetMicrosoftStoreLoopbackAsync(cancellationToken).ConfigureAwait(false);
                return await DiagnoseMicrosoftStoreAsync(cancellationToken).ConfigureAwait(false);
            default:
                throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported Windows diagnostic target.");
        }
    }

    /// <summary>Diagnoses WSL availability and proxy environment bridging.</summary>
    /// <param name="cancellationToken">Cancels the WSL status process.</param>
    /// <returns>WSL diagnostic result.</returns>
    private static async Task<WindowsDiagnosticResult> DiagnoseWslAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync("wsl.exe", ["--status"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        bool isAvailable = result.ExitCode == 0;
        string proxyUrl = BuildLocalProxyUrl();
        string wslEnv = Environment.GetEnvironmentVariable("WSLENV", EnvironmentVariableTarget.User) ?? string.Empty;
        bool hasBridge = ContainsAllWslEnvTokens(wslEnv);
        bool hasProxyEnvironment = IsProxyEnvironmentConfigured(proxyUrl);
        bool isHealthy = isAvailable && hasBridge && hasProxyEnvironment;
        string message = isHealthy
            ? "WSL 代理桥接已配置。"
            : isAvailable
                ? hasBridge ? "WSL 已配置桥接，但用户代理环境变量未指向 Clash#。" : "WSL 可用，但未配置代理桥接。"
                : "WSL 不可用或未安装。";
        string detail = isAvailable ? $"WSLENV={wslEnv}; {BuildProxyEnvironmentDetail()}" : result.Error;

        return new WindowsDiagnosticResult(WindowsDiagnosticTarget.Wsl, "WSL", isHealthy, message, detail);
    }

    /// <summary>Diagnoses terminal proxy environment variables for newly launched shells.</summary>
    /// <returns>Terminal diagnostic result.</returns>
    private static WindowsDiagnosticResult DiagnoseTerminal()
    {
        string proxyUrl = BuildLocalProxyUrl();
        bool isHealthy = IsProxyEnvironmentConfigured(proxyUrl);
        string message = isHealthy ? "终端代理环境变量已配置。" : "终端代理环境变量未指向 Clash#。";
        string detail = BuildProxyEnvironmentDetail();

        return new WindowsDiagnosticResult(WindowsDiagnosticTarget.Terminal, "终端", isHealthy, message, detail);
    }

    /// <summary>Diagnoses Microsoft Store loopback exemption state.</summary>
    /// <param name="cancellationToken">Cancels the CheckNetIsolation process.</param>
    /// <returns>Microsoft Store diagnostic result.</returns>
    private static async Task<WindowsDiagnosticResult> DiagnoseMicrosoftStoreAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync("CheckNetIsolation.exe", ["LoopbackExempt", "-s"], TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        bool isHealthy = result.Output.Contains(MicrosoftStorePackageFamilyName, StringComparison.OrdinalIgnoreCase);
        string message = isHealthy ? "Microsoft Store 已允许访问本机代理。" : "Microsoft Store 未配置本机代理访问豁免。";
        string detail = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;

        return new WindowsDiagnosticResult(WindowsDiagnosticTarget.MicrosoftStore, "Microsoft Store", isHealthy, message, detail);
    }

    /// <summary>Applies terminal proxy environment variables for newly launched shells.</summary>
    private static void ApplyTerminalProxyEnvironment()
    {
        string proxyUrl = BuildLocalProxyUrl();
        Environment.SetEnvironmentVariable("HTTP_PROXY", proxyUrl, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", proxyUrl, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("ALL_PROXY", proxyUrl, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("NO_PROXY", NoProxyValue, EnvironmentVariableTarget.User);
    }

    /// <summary>Applies WSL proxy environment variable bridging through WSLENV.</summary>
    private static void ApplyWslProxyBridge()
    {
        string currentValue = Environment.GetEnvironmentVariable("WSLENV", EnvironmentVariableTarget.User) ?? string.Empty;
        List<string> tokens = [.. currentValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        foreach (string token in WslEnvProxyTokens)
        {
            if (!tokens.Exists(value => StringComparer.OrdinalIgnoreCase.Equals(value, token)))
            {
                tokens.Add(token);
            }
        }

        Environment.SetEnvironmentVariable("WSLENV", string.Join(':', tokens), EnvironmentVariableTarget.User);
    }

    /// <summary>Removes WSL proxy bridge tokens from the user's WSLENV value.</summary>
    private static void ResetWslProxyBridge()
    {
        string currentValue = Environment.GetEnvironmentVariable("WSLENV", EnvironmentVariableTarget.User) ?? string.Empty;
        List<string> tokens = [.. currentValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        tokens.RemoveAll(token => Array.Exists(WslEnvProxyTokens, proxyToken => StringComparer.OrdinalIgnoreCase.Equals(proxyToken, token)));
        Environment.SetEnvironmentVariable("WSLENV", tokens.Count == 0 ? null : string.Join(':', tokens), EnvironmentVariableTarget.User);
    }

    /// <summary>Clears terminal proxy environment variables for newly launched shells.</summary>
    private static void ResetTerminalProxyEnvironment()
    {
        Environment.SetEnvironmentVariable("HTTP_PROXY", null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("HTTPS_PROXY", null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("ALL_PROXY", null, EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("NO_PROXY", null, EnvironmentVariableTarget.User);
    }

    /// <summary>Applies Microsoft Store loopback exemption through CheckNetIsolation.</summary>
    /// <param name="cancellationToken">Cancels the CheckNetIsolation process.</param>
    /// <exception cref="InvalidOperationException">CheckNetIsolation exits unsuccessfully.</exception>
    private static async Task ApplyMicrosoftStoreLoopbackAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            "CheckNetIsolation.exe",
            ["LoopbackExempt", "-a", "-n=" + MicrosoftStorePackageFamilyName],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
    }

    /// <summary>Removes Microsoft Store loopback exemption through CheckNetIsolation.</summary>
    /// <param name="cancellationToken">Cancels the CheckNetIsolation process.</param>
    /// <exception cref="InvalidOperationException">CheckNetIsolation exits unsuccessfully.</exception>
    private static async Task ResetMicrosoftStoreLoopbackAsync(CancellationToken cancellationToken)
    {
        ProcessResult result = await RunProcessAsync(
            "CheckNetIsolation.exe",
            ["LoopbackExempt", "-d", "-n=" + MicrosoftStorePackageFamilyName],
            TimeSpan.FromSeconds(10),
            cancellationToken).ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
    }

    /// <summary>Builds the local proxy URL used by Windows diagnostic apply actions.</summary>
    /// <returns>HTTP proxy URL using the configured mixed port.</returns>
    private static string BuildLocalProxyUrl()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{AppSettingsService.Instance.MixedPort}");
    }

    /// <summary>Returns whether user-level proxy environment variables match the configured Clash# endpoint.</summary>
    /// <param name="proxyUrl">Expected local proxy URL. Must not be null.</param>
    /// <returns>True when HTTP, HTTPS, ALL, and NO_PROXY values are configured for Clash#.</returns>
    private static bool IsProxyEnvironmentConfigured(string proxyUrl)
    {
        ArgumentNullException.ThrowIfNull(proxyUrl);

        string httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;
        string httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;
        string allProxy = Environment.GetEnvironmentVariable("ALL_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;
        string noProxy = Environment.GetEnvironmentVariable("NO_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;

        return StringComparer.OrdinalIgnoreCase.Equals(httpProxy, proxyUrl)
            && StringComparer.OrdinalIgnoreCase.Equals(httpsProxy, proxyUrl)
            && StringComparer.OrdinalIgnoreCase.Equals(allProxy, proxyUrl)
            && ContainsNoProxyLoopback(noProxy);
    }

    /// <summary>Builds diagnostic detail for user-level proxy environment variables.</summary>
    /// <returns>A compact environment variable summary.</returns>
    private static string BuildProxyEnvironmentDetail()
    {
        string httpProxy = Environment.GetEnvironmentVariable("HTTP_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;
        string httpsProxy = Environment.GetEnvironmentVariable("HTTPS_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;
        string allProxy = Environment.GetEnvironmentVariable("ALL_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;
        string noProxy = Environment.GetEnvironmentVariable("NO_PROXY", EnvironmentVariableTarget.User) ?? string.Empty;

        return $"HTTP_PROXY={httpProxy}; HTTPS_PROXY={httpsProxy}; ALL_PROXY={allProxy}; NO_PROXY={noProxy}";
    }

    /// <summary>Returns whether NO_PROXY contains the loopback exclusions required by Clash#.</summary>
    /// <param name="noProxy">NO_PROXY value. Must not be null.</param>
    /// <returns>True when localhost, IPv4 loopback, and IPv6 loopback are excluded.</returns>
    private static bool ContainsNoProxyLoopback(string noProxy)
    {
        ArgumentNullException.ThrowIfNull(noProxy);

        string[] tokens = noProxy.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Array.Exists(tokens, token => StringComparer.OrdinalIgnoreCase.Equals(token, "localhost"))
            && Array.Exists(tokens, token => StringComparer.OrdinalIgnoreCase.Equals(token, "127.0.0.1"))
            && Array.Exists(tokens, token => StringComparer.OrdinalIgnoreCase.Equals(token, "::1"));
    }

    /// <summary>Returns whether WSLENV contains all proxy bridge tokens.</summary>
    /// <param name="wslEnv">WSLENV value. Must not be null.</param>
    /// <returns>True when all required tokens are present.</returns>
    private static bool ContainsAllWslEnvTokens(string wslEnv)
    {
        ArgumentNullException.ThrowIfNull(wslEnv);

        string[] tokens = wslEnv.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string token in WslEnvProxyTokens)
        {
            if (!Array.Exists(tokens, value => StringComparer.OrdinalIgnoreCase.Equals(value, token)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Runs an external process with redirected output and timeout.</summary>
    /// <param name="fileName">Executable name. Must not be null.</param>
    /// <param name="arguments">Argument list. Must not be null.</param>
    /// <param name="timeout">Process timeout.</param>
    /// <param name="cancellationToken">Cancels the process.</param>
    /// <returns>Process result containing exit code and captured output.</returns>
    private static async Task<ProcessResult> RunProcessAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
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
                return new ProcessResult(-1, string.Empty, "Process could not be started.");
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return new ProcessResult(-1, string.Empty, exception.Message);
        }

        using CancellationTokenSource timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        try
        {
            Task<string> outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            Task<string> errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);

            return new ProcessResult(
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

        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
        }
    }

    /// <summary>Captured process result.</summary>
    /// <param name="ExitCode">Process exit code.</param>
    /// <param name="Output">Standard output text; never null.</param>
    /// <param name="Error">Standard error text; never null.</param>
    private readonly record struct ProcessResult(int ExitCode, string Output, string Error);
}
