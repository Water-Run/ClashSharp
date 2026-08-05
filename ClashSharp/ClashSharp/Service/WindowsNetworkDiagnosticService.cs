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
/// Thread safety: Apply and reset transactions are serialized per service instance; process launches are delegated to an injected runner.
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

    /// <summary>User environment variables written by terminal and WSL repair actions.</summary>
    private static readonly string[] ProxyEnvironmentVariableNames = ["HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY"];

    private readonly IWindowsDiagnosticSettings _settings;

    private readonly IWindowsDiagnosticEnvironment _environment;

    private readonly IWindowsDiagnosticProcessRunner _processRunner;

    private readonly IWindowsDiagnosticMutationJournalStore _mutationJournal;

    private readonly Func<string, string> _getString;

    /// <summary>Serializes journal-backed apply and reset transactions.</summary>
    private readonly SemaphoreSlim _mutationGate = new(1, 1);

    /// <summary>Initializes the Windows network diagnostic service.</summary>
    internal WindowsNetworkDiagnosticService(
        IWindowsDiagnosticSettings settings,
        IWindowsDiagnosticEnvironment environment,
        IWindowsDiagnosticProcessRunner processRunner,
        IWindowsDiagnosticMutationJournalStore mutationJournal,
        Func<string, string> getString)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        _mutationJournal = mutationJournal ?? throw new ArgumentNullException(nameof(mutationJournal));
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
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (target)
            {
                case WindowsDiagnosticTarget.Wsl:
                    ApplyEnvironmentMutations(WindowsDiagnosticMutationOwner.Wsl, includeWslBridge: true);
                    return await DiagnoseWslAsync(cancellationToken).ConfigureAwait(false);
                case WindowsDiagnosticTarget.Terminal:
                    ApplyEnvironmentMutations(WindowsDiagnosticMutationOwner.Terminal, includeWslBridge: false);
                    return DiagnoseTerminal();
                case WindowsDiagnosticTarget.MicrosoftStore:
                    return await ApplyOwnedMicrosoftStoreLoopbackAsync(cancellationToken).ConfigureAwait(false);
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported Windows diagnostic target.");
            }
        }
        finally
        {
            _mutationGate.Release();
        }
    }

    /// <summary>Resets one Windows-native network repair action.</summary>
    /// <param name="target">Diagnostic target to reset.</param>
    /// <param name="cancellationToken">Cancels external process actions.</param>
    /// <returns>Diagnostic result after the reset action.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="target"/> is not supported.</exception>
    public async Task<WindowsDiagnosticResult> ResetAsync(WindowsDiagnosticTarget target, CancellationToken cancellationToken)
    {
        await _mutationGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            switch (target)
            {
                case WindowsDiagnosticTarget.Wsl:
                    ResetEnvironmentMutations(WindowsDiagnosticMutationOwner.Wsl, includeWslBridge: true);
                    return await DiagnoseWslAsync(cancellationToken).ConfigureAwait(false);
                case WindowsDiagnosticTarget.Terminal:
                    ResetEnvironmentMutations(WindowsDiagnosticMutationOwner.Terminal, includeWslBridge: false);
                    return DiagnoseTerminal();
                case WindowsDiagnosticTarget.MicrosoftStore:
                    return await ResetOwnedMicrosoftStoreLoopbackAsync(cancellationToken).ConfigureAwait(false);
                default:
                    throw new ArgumentOutOfRangeException(nameof(target), target, "Unsupported Windows diagnostic target.");
            }
        }
        finally
        {
            _mutationGate.Release();
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

    /// <summary>Captures and applies environment mutations owned by one diagnostic target.</summary>
    private void ApplyEnvironmentMutations(WindowsDiagnosticMutationOwner owner, bool includeWslBridge)
    {
        WindowsDiagnosticMutationJournal journal = _mutationJournal.Read();
        Dictionary<string, WindowsDiagnosticEnvironmentMutation> mutations = new(
            journal.EnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string> desiredValues = BuildDesiredEnvironmentValues(includeWslBridge);

        foreach ((string name, string desiredValue) in desiredValues)
        {
            string? currentValue = _environment.GetUserEnvironmentVariable(name);
            if (mutations.TryGetValue(name, out WindowsDiagnosticEnvironmentMutation? existing))
            {
                bool externalChange = !IsOwnedEnvironmentValue(currentValue, existing);
                mutations[name] = existing with
                {
                    BaselineExists = externalChange ? currentValue is not null : existing.BaselineExists,
                    BaselineValue = externalChange ? currentValue : existing.BaselineValue,
                    AppliedValue = currentValue,
                    Owners = existing.Owners | owner,
                    Phase = WindowsDiagnosticMutationPhase.Applying,
                    PendingAppliedValue = desiredValue,
                };
            }
            else
            {
                mutations[name] = new WindowsDiagnosticEnvironmentMutation(
                    currentValue is not null,
                    currentValue,
                    currentValue,
                    owner,
                    WindowsDiagnosticMutationPhase.Applying,
                    desiredValue);
            }
        }

        WindowsDiagnosticMutationJournal plannedJournal = journal with { EnvironmentVariables = mutations };

        // Persist every prior/pending pair before the first environment variable changes.
        _mutationJournal.Write(plannedJournal);

        foreach ((string name, string desiredValue) in desiredValues)
        {
            SetEnvironment(name, desiredValue);
        }

        foreach (string name in desiredValues.Keys)
        {
            WindowsDiagnosticEnvironmentMutation mutation = mutations[name];
            mutations[name] = mutation with
            {
                AppliedValue = mutation.PendingAppliedValue,
                Phase = WindowsDiagnosticMutationPhase.Applied,
                PendingAppliedValue = null,
            };
        }

        _mutationJournal.Write(journal with { EnvironmentVariables = mutations });
    }

    /// <summary>Restores environment baselines only while Clash# still owns the last applied values.</summary>
    private void ResetEnvironmentMutations(WindowsDiagnosticMutationOwner owner, bool includeWslBridge)
    {
        WindowsDiagnosticMutationJournal journal = _mutationJournal.Read();
        Dictionary<string, WindowsDiagnosticEnvironmentMutation> mutations = new(
            journal.EnvironmentVariables,
            StringComparer.OrdinalIgnoreCase);
        List<string> names = [.. ProxyEnvironmentVariableNames];
        if (includeWslBridge)
        {
            names.Add("WSLENV");
        }

        foreach (string name in names)
        {
            if (!mutations.TryGetValue(name, out WindowsDiagnosticEnvironmentMutation? mutation)
                || (mutation.Owners & owner) == 0)
            {
                continue;
            }

            WindowsDiagnosticMutationOwner remainingOwners = mutation.Owners & ~owner;
            if (remainingOwners != WindowsDiagnosticMutationOwner.None)
            {
                string? remainingOwnerValue = _environment.GetUserEnvironmentVariable(name);
                mutations[name] = FinalizeObservedEnvironmentMutation(
                    mutation,
                    remainingOwnerValue,
                    remainingOwners);
                continue;
            }

            string? currentValue = _environment.GetUserEnvironmentVariable(name);
            if (IsOwnedEnvironmentValue(currentValue, mutation))
            {
                SetEnvironment(name, mutation.BaselineExists ? mutation.BaselineValue : null);
            }

            mutations.Remove(name);
        }

        _mutationJournal.Write(journal with { EnvironmentVariables = mutations });
    }

    /// <summary>Returns whether a value matches either durable side of an in-flight apply.</summary>
    private static bool IsOwnedEnvironmentValue(
        string? currentValue,
        WindowsDiagnosticEnvironmentMutation mutation)
    {
        return StringComparer.Ordinal.Equals(currentValue, mutation.AppliedValue)
            || mutation.Phase == WindowsDiagnosticMutationPhase.Applying
                && StringComparer.Ordinal.Equals(currentValue, mutation.PendingAppliedValue);
    }

    /// <summary>Collapses an in-flight value while another diagnostic owner remains.</summary>
    private static WindowsDiagnosticEnvironmentMutation FinalizeObservedEnvironmentMutation(
        WindowsDiagnosticEnvironmentMutation mutation,
        string? currentValue,
        WindowsDiagnosticMutationOwner remainingOwners)
    {
        if (mutation.Phase != WindowsDiagnosticMutationPhase.Applying)
        {
            return mutation with { Owners = remainingOwners };
        }

        string appliedValue = StringComparer.Ordinal.Equals(currentValue, mutation.PendingAppliedValue)
            ? mutation.PendingAppliedValue!
            : mutation.AppliedValue ?? mutation.PendingAppliedValue!;
        return mutation with
        {
            AppliedValue = appliedValue,
            Owners = remainingOwners,
            Phase = WindowsDiagnosticMutationPhase.Applied,
            PendingAppliedValue = null,
        };
    }

    /// <summary>Builds the exact environment values written by one repair action.</summary>
    private Dictionary<string, string> BuildDesiredEnvironmentValues(bool includeWslBridge)
    {
        string proxyUrl = BuildLocalProxyUrl();
        Dictionary<string, string> desiredValues = new(StringComparer.OrdinalIgnoreCase)
        {
            ["HTTP_PROXY"] = proxyUrl,
            ["HTTPS_PROXY"] = proxyUrl,
            ["ALL_PROXY"] = proxyUrl,
            ["NO_PROXY"] = NoProxyValue,
        };

        if (includeWslBridge)
        {
            desiredValues["WSLENV"] = BuildWslEnvironmentValue(GetEnvironment("WSLENV"));
        }

        return desiredValues;
    }

    /// <summary>Adds the required proxy bridge tokens while retaining unrelated WSLENV entries.</summary>
    private static string BuildWslEnvironmentValue(string currentValue)
    {
        List<string> tokens = [.. currentValue.Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        foreach (string token in WslEnvProxyTokens)
        {
            if (!tokens.Exists(value => StringComparer.OrdinalIgnoreCase.Equals(value, token)))
            {
                tokens.Add(token);
            }
        }

        return string.Join(':', tokens);
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

    /// <summary>Captures Store exemption baseline before applying the Clash#-owned state.</summary>
    private async Task<WindowsDiagnosticResult> ApplyOwnedMicrosoftStoreLoopbackAsync(CancellationToken cancellationToken)
    {
        WindowsDiagnosticMutationJournal journal = _mutationJournal.Read();
        bool currentState = await GetMicrosoftStoreLoopbackStateAsync(cancellationToken).ConfigureAwait(false);
        bool baselineState = journal.MicrosoftStore is { } existing
            && IsOwnedMicrosoftStoreState(currentState, existing)
                ? existing.BaselinePresent
                : currentState;
        WindowsDiagnosticStoreMutation mutation = new(
            baselineState,
            currentState,
            WindowsDiagnosticMutationPhase.Applying,
            PendingAppliedPresent: true);

        // Persist the recovery proof before CheckNetIsolation changes the exemption.
        _mutationJournal.Write(journal with { MicrosoftStore = mutation });
        if (!currentState)
        {
            await ApplyMicrosoftStoreLoopbackAsync(cancellationToken).ConfigureAwait(false);
        }

        _mutationJournal.Write(journal with
        {
            MicrosoftStore = mutation with
            {
                AppliedPresent = true,
                Phase = WindowsDiagnosticMutationPhase.Applied,
                PendingAppliedPresent = null,
            },
        });

        return await DiagnoseMicrosoftStoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Restores Store exemption baseline only while the last Clash#-applied state remains present.</summary>
    private async Task<WindowsDiagnosticResult> ResetOwnedMicrosoftStoreLoopbackAsync(CancellationToken cancellationToken)
    {
        WindowsDiagnosticMutationJournal journal = _mutationJournal.Read();
        if (journal.MicrosoftStore is not { } mutation)
        {
            return await DiagnoseMicrosoftStoreAsync(cancellationToken).ConfigureAwait(false);
        }

        bool currentState = await GetMicrosoftStoreLoopbackStateAsync(cancellationToken).ConfigureAwait(false);
        if (IsOwnedMicrosoftStoreState(currentState, mutation) && currentState != mutation.BaselinePresent)
        {
            if (mutation.BaselinePresent)
            {
                await ApplyMicrosoftStoreLoopbackAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ResetMicrosoftStoreLoopbackAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        _mutationJournal.Write(journal with { MicrosoftStore = null });
        return await DiagnoseMicrosoftStoreAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Returns whether the Store state matches the prior or pending owned state.</summary>
    private static bool IsOwnedMicrosoftStoreState(
        bool currentState,
        WindowsDiagnosticStoreMutation mutation)
    {
        return currentState == mutation.AppliedPresent
            || mutation.Phase == WindowsDiagnosticMutationPhase.Applying
                && currentState == mutation.PendingAppliedPresent;
    }

    /// <summary>Reads the Microsoft Store loopback exemption state and requires a successful system query.</summary>
    private async Task<bool> GetMicrosoftStoreLoopbackStateAsync(CancellationToken cancellationToken)
    {
        WindowsDiagnosticProcessResult result = await _processRunner
            .RunAsync("CheckNetIsolation.exe", ["LoopbackExempt", "-s"], TimeSpan.FromSeconds(5), cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Error) ? result.Output : result.Error);
        }

        return result.Output.Contains(MicrosoftStorePackageFamilyName, StringComparison.OrdinalIgnoreCase);
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
