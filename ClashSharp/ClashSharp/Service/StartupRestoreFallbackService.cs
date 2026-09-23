using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.Service;

/// <summary>Manages the packaged current-user login task for stale proxy cleanup.</summary>
/// <remarks>Windows owns the registration and removes it with the package. UI callers must use the asynchronous methods.</remarks>
public sealed partial class StartupRestoreFallbackService
{
    /// <summary>Command-line switch that selects the one-shot proxy restoration path.</summary>
    public const string HelperArgument = "--restore-proxy-on-startup";

    /// <summary>Independent restore task declared in the package manifest.</summary>
    public const string TaskId = "ClashSharpProxyRestoreFallback";

    private readonly StartupLaunchService _startup;

    internal StartupRestoreFallbackService(StartupLaunchService startup)
    {
        _startup = startup ?? throw new ArgumentNullException(nameof(startup));
    }

    /// <summary>Reads the actual Windows startup-task state without blocking the UI.</summary>
    public async Task<StartupRestoreFallbackStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        StartupLaunchTaskState? state = await _startup.TryGetStateAsync(cancellationToken).ConfigureAwait(false);
        if (state is null)
        {
            throw new InvalidOperationException("The startup restore task state could not be read.");
        }

        bool enabled = state == StartupLaunchTaskState.Enabled;
        return new StartupRestoreFallbackStatus(enabled, enabled ? "ClashSharp.exe " + HelperArgument : string.Empty);
    }

    /// <summary>Enables and verifies the independent login restore task.</summary>
    public Task RegisterAsync(CancellationToken cancellationToken) =>
        _startup.SetEnabledAsync(true, cancellationToken);

    /// <summary>Disables and verifies the independent login restore task.</summary>
    public Task RemoveRegistrationAsync(CancellationToken cancellationToken) =>
        _startup.SetEnabledAsync(false, cancellationToken);
}
