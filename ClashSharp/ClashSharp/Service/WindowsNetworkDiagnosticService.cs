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
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Provides settings required by Windows-native diagnostics.</summary>
internal interface IWindowsDiagnosticSettings
{
    /// <summary>Gets the local mixed HTTP/SOCKS port used by environment proxy URLs.</summary>
    int MixedPort { get; }
}

/// <summary>Reads and writes user-level environment variables for Windows-native diagnostics.</summary>
internal interface IWindowsDiagnosticEnvironment
{
    /// <summary>Gets one user-level environment variable.</summary>
    string? GetUserEnvironmentVariable(string name);

    /// <summary>Sets or clears one user-level environment variable.</summary>
    void SetUserEnvironmentVariable(string name, string? value);
}

/// <summary>Runs external Windows diagnostic processes.</summary>
internal interface IWindowsDiagnosticProcessRunner
{
    /// <summary>Runs a process with the supplied arguments and timeout.</summary>
    Task<WindowsDiagnosticProcessResult> RunAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

/// <summary>Captured process result.</summary>
/// <param name="ExitCode">Process exit code.</param>
/// <param name="Output">Standard output text; never null.</param>
/// <param name="Error">Standard error text; never null.</param>
internal readonly record struct WindowsDiagnosticProcessResult(int ExitCode, string Output, string Error);

/// <summary>Provides independent WSL, terminal, and Microsoft Store network diagnostics, apply, and reset actions.</summary>
/// <remarks>
/// Invariants: Each target diagnosis reports its own readiness; WSL repair also writes proxy environment variables required by WSLENV bridging.
/// Thread safety: Stateless service; process launches are delegated to an injected runner.
/// Side effects: Apply and reset methods may update user environment variables or Microsoft Store loopback exemptions through injected dependencies.
/// </remarks>
public sealed partial class WindowsNetworkDiagnosticService
{
    /// <summary>Microsoft Store package family name used by CheckNetIsolation.</summary>
    private const string MicrosoftStorePackageFamilyName = "Microsoft.WindowsStore_8wekyb3d8bbwe";

    /// <summary>WSLENV token set used to bridge proxy variables into WSL distributions.</summary>
    private static readonly string[] WslEnvProxyTokens = ["HTTP_PROXY/u", "HTTPS_PROXY/u", "ALL_PROXY/u", "NO_PROXY/u"];

    /// <summary>Loopback hosts excluded from terminal and WSL proxy routing.</summary>
    private const string NoProxyValue = "localhost,127.0.0.1,::1";

    private readonly IWindowsDiagnosticSettings _settings;

    private readonly IWindowsDiagnosticEnvironment _environment;

    private readonly IWindowsDiagnosticProcessRunner _processRunner;

    private readonly Func<string, string> _getString;

    /// <summary>Initializes the Windows network diagnostic service.</summary>
    internal WindowsNetworkDiagnosticService(
        IWindowsDiagnosticSettings settings,
        IWindowsDiagnosticEnvironment environment,
        IWindowsDiagnosticProcessRunner processRunner,
        Func<string, string> getString)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _getString = getString ?? throw new ArgumentNullException(nameof(getString));
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
    private async Task<WindowsDiagnosticResult> DiagnoseWslAsync(CancellationToken cancellationToken)
    {
        WindowsDiagnosticProcessResult result = await _processRunner
            .RunAsync("wsl.exe", ["--status"], TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        bool isAvailable = result.ExitCode == 0;
        string proxyUrl = BuildLocalProxyUrl();
        string wslEnv = GetEnvironment("WSLENV");
        bool hasBridge = ContainsAllWslEnvTokens(wslEnv);
        bool hasProxyEnvironment = IsProxyEnvironmentConfigured(proxyUrl);
        bool isHealthy = isAvailable && hasBridge && hasProxyEnvironment;
        string message = ResolveWslMessage(isHealthy, isAvailable, hasBridge);
        string detail = isAvailable ? $"WSLENV={wslEnv}; {BuildProxyEnvironmentDetail()}" : result.Error;

        return new WindowsDiagnosticResult(WindowsDiagnosticTarget.Wsl, "WSL", isHealthy, message, detail);
    }

    /// <summary>Diagnoses terminal proxy environment variables for newly launched shells.</summary>
    /// <returns>Terminal diagnostic result.</returns>
    private WindowsDiagnosticResult DiagnoseTerminal()
    {
        string proxyUrl = BuildLocalProxyUrl();
        bool isHealthy = IsProxyEnvironmentConfigured(proxyUrl);
        string message = isHealthy
            ? GetString("WindowsDiagnostic.Terminal.Ready")
            : GetString("WindowsDiagnostic.Terminal.ProxyEnvironmentMissing");
        string detail = BuildProxyEnvironmentDetail();

        return new WindowsDiagnosticResult(WindowsDiagnosticTarget.Terminal, GetString("WindowsDiagnostic.Target.Terminal"), isHealthy, message, detail);
    }

    /// <summary>Diagnoses Microsoft Store loopback exemption state.</summary>
    /// <param name="cancellationToken">Cancels the CheckNetIsolation process.</param>
    /// <returns>Microsoft Store diagnostic result.</returns>
    private async Task<WindowsDiagnosticResult> DiagnoseMicrosoftStoreAsync(CancellationToken cancellationToken)
    {
        WindowsDiagnosticProcessResult result = await _processRunner
            .RunAsync("CheckNetIsolation.exe", ["LoopbackExempt", "-s"], TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        bool isHealthy = result.Output.Contains(MicrosoftStorePackageFamilyName, StringComparison.OrdinalIgnoreCase);
        string message = isHealthy
            ? GetString("WindowsDiagnostic.MicrosoftStore.Ready")
            : GetString("WindowsDiagnostic.MicrosoftStore.LoopbackMissing");
        string detail = string.IsNullOrWhiteSpace(result.Output) ? result.Error : result.Output;

        return new WindowsDiagnosticResult(WindowsDiagnosticTarget.MicrosoftStore, "Microsoft Store", isHealthy, message, detail);
    }

    /// <summary>Resolves WSL diagnostic status text.</summary>
    private string ResolveWslMessage(bool isHealthy, bool isAvailable, bool hasBridge)
    {
        if (isHealthy)
        {
            return GetString("WindowsDiagnostic.Wsl.Ready");
        }

        if (!isAvailable)
        {
            return GetString("WindowsDiagnostic.Wsl.Unavailable");
        }

        return hasBridge
            ? GetString("WindowsDiagnostic.Wsl.ProxyEnvironmentMissing")
            : GetString("WindowsDiagnostic.Wsl.BridgeMissing");
    }

    /// <summary>Applies terminal proxy environment variables for newly launched shells.</summary>
    private void ApplyTerminalProxyEnvironment()
    {
        string proxyUrl = BuildLocalProxyUrl();
        SetEnvironment("HTTP_PROXY", proxyUrl);
        SetEnvironment("HTTPS_PROXY", proxyUrl);
        SetEnvironment("ALL_PROXY", proxyUrl);
        SetEnvironment("NO_PROXY", NoProxyValue);
    }

    /// <summary>Applies WSL proxy environment variable bridging through WSLENV.</summary>
    private void ApplyWslProxyBridge()
    {
        string currentValue = GetEnvironment("WSLENV");
        List<string> tokens = [.. currentValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        foreach (string token in WslEnvProxyTokens)
        {
            if (!tokens.Exists(value => StringComparer.OrdinalIgnoreCase.Equals(value, token)))
            {
                tokens.Add(token);
            }
        }

        SetEnvironment("WSLENV", string.Join(':', tokens));
    }

    /// <summary>Removes WSL proxy bridge tokens from the user's WSLENV value.</summary>
    private void ResetWslProxyBridge()
    {
        string currentValue = GetEnvironment("WSLENV");
        List<string> tokens = [.. currentValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        tokens.RemoveAll(token => Array.Exists(WslEnvProxyTokens, proxyToken => StringComparer.OrdinalIgnoreCase.Equals(proxyToken, token)));
        SetEnvironment("WSLENV", tokens.Count == 0 ? null : string.Join(':', tokens));
    }

    /// <summary>Clears terminal proxy environment variables for newly launched shells.</summary>
    private void ResetTerminalProxyEnvironment()
    {
        SetEnvironment("HTTP_PROXY", null);
        SetEnvironment("HTTPS_PROXY", null);
        SetEnvironment("ALL_PROXY", null);
        SetEnvironment("NO_PROXY", null);
    }

    /// <summary>Applies Microsoft Store loopback exemption through CheckNetIsolation.</summary>
    /// <param name="cancellationToken">Cancels the CheckNetIsolation process.</param>
    /// <exception cref="InvalidOperationException">CheckNetIsolation exits unsuccessfully.</exception>
    private async Task ApplyMicrosoftStoreLoopbackAsync(CancellationToken cancellationToken)
    {
        WindowsDiagnosticProcessResult result = await _processRunner
            .RunAsync(
                "CheckNetIsolation.exe",
                ["LoopbackExempt", "-a", "-n=" + MicrosoftStorePackageFamilyName],
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
    }

    /// <summary>Removes Microsoft Store loopback exemption through CheckNetIsolation.</summary>
    /// <param name="cancellationToken">Cancels the CheckNetIsolation process.</param>
    /// <exception cref="InvalidOperationException">CheckNetIsolation exits unsuccessfully.</exception>
    private async Task ResetMicrosoftStoreLoopbackAsync(CancellationToken cancellationToken)
    {
        WindowsDiagnosticProcessResult result = await _processRunner
            .RunAsync(
                "CheckNetIsolation.exe",
                ["LoopbackExempt", "-d", "-n=" + MicrosoftStorePackageFamilyName],
                TimeSpan.FromSeconds(10),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }
    }

    /// <summary>Builds the local proxy URL used by Windows diagnostic apply actions.</summary>
    /// <returns>HTTP proxy URL using the configured mixed port.</returns>
    private string BuildLocalProxyUrl()
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"http://127.0.0.1:{_settings.MixedPort}");
    }

    private string GetString(string key)
    {
        return _getString(key);
    }

    private string GetEnvironment(string name)
    {
        return _environment.GetUserEnvironmentVariable(name) ?? string.Empty;
    }

    private void SetEnvironment(string name, string? value)
    {
        _environment.SetUserEnvironmentVariable(name, value);
    }

    /// <summary>Returns whether user-level proxy environment variables match the configured Clash# endpoint.</summary>
    /// <param name="proxyUrl">Expected local proxy URL. Must not be null.</param>
    /// <returns>True when HTTP, HTTPS, ALL, and NO_PROXY values are configured for Clash#.</returns>
    private bool IsProxyEnvironmentConfigured(string proxyUrl)
    {
        ArgumentNullException.ThrowIfNull(proxyUrl);

        string httpProxy = GetEnvironment("HTTP_PROXY");
        string httpsProxy = GetEnvironment("HTTPS_PROXY");
        string allProxy = GetEnvironment("ALL_PROXY");
        string noProxy = GetEnvironment("NO_PROXY");

        return StringComparer.OrdinalIgnoreCase.Equals(httpProxy, proxyUrl)
            && StringComparer.OrdinalIgnoreCase.Equals(httpsProxy, proxyUrl)
            && StringComparer.OrdinalIgnoreCase.Equals(allProxy, proxyUrl)
            && ContainsNoProxyLoopback(noProxy);
    }

    /// <summary>Builds diagnostic detail for user-level proxy environment variables.</summary>
    /// <returns>A compact environment variable summary.</returns>
    private string BuildProxyEnvironmentDetail()
    {
        string httpProxy = GetEnvironment("HTTP_PROXY");
        string httpsProxy = GetEnvironment("HTTPS_PROXY");
        string allProxy = GetEnvironment("ALL_PROXY");
        string noProxy = GetEnvironment("NO_PROXY");

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
}
