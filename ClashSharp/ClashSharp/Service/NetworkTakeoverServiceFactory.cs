using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Diagnostics;
using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

public sealed partial class NetworkTakeoverService
{
    private static readonly Lazy<NetworkTakeoverService> DefaultInstance = new(NetworkTakeoverServiceFactory.CreateDefault);

    /// <summary>Shared singleton instance created on first access.</summary>
    /// <value>A non-null <see cref="NetworkTakeoverService"/> instance.</value>
    public static NetworkTakeoverService Instance => DefaultInstance.Value;
}

/// <summary>Creates network takeover service instances with production dependencies.</summary>
internal static class NetworkTakeoverServiceFactory
{
    private static int _crashRecoverySubscribed;

    /// <summary>Creates the compatibility service used only by the durable network adapter.</summary>
    /// <returns>A network takeover service wired to mihomo, Windows proxy, and localization resources.</returns>
    public static NetworkTakeoverService CreateDefault()
    {
        MihomoCoreService core = MihomoCoreService.Instance;
        WindowsProxyService windowsProxy = WindowsProxyService.Instance;
        SubscribeCrashRecoveryOnce(core, windowsProxy);
        return new NetworkTakeoverService(
            new NetworkTakeoverCoreConfigurationAdapter(CoreConfigurationService.Instance),
            new NetworkTakeoverCoreAdapter(core),
            new NetworkTakeoverWindowsProxyAdapter(windowsProxy),
            new NetworkTakeoverMihomoServiceAdapter(MihomoServiceManager.Instance),
            new NetworkTakeoverProxyRecoveryAdapter(ProxyRecoveryService.Instance),
            new NetworkTakeoverReadinessAdapter(MihomoControllerClient.Instance),
            LocalizationService.Instance.GetString,
            ProxySelectionService.Instance,
            FlushTrafficBeforeTransitionAsync);
    }

    /// <summary>Best-effort final accounting is bounded and cannot prevent network ownership release.</summary>
    private static async Task FlushTrafficBeforeTransitionAsync(CancellationToken cancellationToken)
    {
        if (!MihomoCoreService.Instance.IsRunning && !MihomoServiceManager.Instance.GetLatestStatus().HasRunningChild)
        {
            return;
        }

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            await RuntimeTrafficRateService.Instance.RefreshAsync(deadline.Token).ConfigureAwait(false);
            await ConnectionSamplingService.Instance.FlushAsync(deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (!ExceptionGraphClassifier.IsProcessFatal(exception)
            && !ExceptionGraphClassifier.IsCallerCancellation(exception, cancellationToken))
        {
            AppendCrashLogSafe("Warning", "Final traffic sample was unavailable before a core transition.", "traffic.final_sample_unavailable");
        }
    }

    /// <summary>Immediately releases owned WinINet state if the App listener disappears.</summary>
    private static void SubscribeCrashRecoveryOnce(
        MihomoCoreService core,
        WindowsProxyService windowsProxy)
    {
        if (Interlocked.Exchange(ref _crashRecoverySubscribed, 1) != 0)
        {
            return;
        }

        core.UnexpectedExit += (_, eventArgs) =>
        {
            string detail = eventArgs.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "unknown";
            try
            {
                windowsProxy.RestoreOwnedProxy();
                AppendCrashLogSafe(
                    "Error",
                    "The App-owned mihomo core exited unexpectedly; owned Windows proxy state was restored.",
                    detail);
            }
            catch (Exception recoveryFailure) when (!ExceptionGraphClassifier.IsProcessFatal(recoveryFailure))
            {
                eventArgs.RecoveryFailure = recoveryFailure;
                AppendCrashLogSafe(
                    "Critical",
                    "The App-owned mihomo core exited and Windows proxy recovery failed.",
                    recoveryFailure.Message);
            }
        };
    }

    private static void AppendCrashLogSafe(string level, string message, string? detail)
    {
        try
        {
            LogStorageService.Instance.AppendLog(level, "MihomoCore", message, detail);
        }
        catch (Exception logFailure) when (!ExceptionGraphClassifier.IsProcessFatal(logFailure))
        {
        }
    }
}

internal sealed class NetworkTakeoverCoreConfigurationAdapter(CoreConfigurationService configuration) : INetworkTakeoverCoreConfiguration
{
    public Task<RuntimeConfigurationTransactionResult> ApplyConfigurationAsync(
        ClashSharpMode mode,
        bool transparentProxyEnabled,
        int mixedPort,
        ICoreConfigurationRuntime runtime,
        CancellationToken cancellationToken)
    {
        return configuration.ApplyRuntimeConfigurationAsync(
            mode,
            transparentProxyEnabled,
            mixedPort,
            runtime,
            cancellationToken);
    }
}

internal sealed class NetworkTakeoverCoreAdapter(MihomoCoreService core) : INetworkTakeoverCore
{
    public bool IsRunning => core.IsRunning;

    public bool IsOwnershipKnown => !core.HasOwnershipFault;

    public void Restart(CoreConfigurationState configurationState)
    {
        // Every production restart is reached only after WindowsProxy.DisableProxy
        // completed; this acknowledges a previously retained crash-recovery fault.
        core.AcknowledgeCrashNetworkRecovery();
        core.Restart(configurationState);
    }

    public void Stop()
    {
        core.Stop();
    }
}

internal sealed class NetworkTakeoverWindowsProxyAdapter(WindowsProxyService windowsProxy) : INetworkTakeoverWindowsProxy
{
    public void DisableProxy()
    {
        windowsProxy.DisableProxy();
    }

    public void EnableProxy(string proxyServer)
    {
        windowsProxy.EnableProxy(proxyServer);
    }
}

internal sealed class NetworkTakeoverMihomoServiceAdapter(MihomoServiceManager serviceManager) : INetworkTakeoverMihomoService
{
    public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        return serviceManager.GetStatusAsync(cancellationToken);
    }

    public Task<MihomoServiceStatus> RestartAsync(
        long generation,
        string configurationHash,
        CancellationToken cancellationToken)
    {
        return serviceManager.RestartAsync(generation, configurationHash, cancellationToken);
    }

    public Task<MihomoServiceStatus> StopAsync(CancellationToken cancellationToken)
    {
        return serviceManager.StopAsync(cancellationToken);
    }
}

internal sealed class NetworkTakeoverProxyRecoveryAdapter(ProxyRecoveryService proxyRecovery) : INetworkTakeoverProxyRecovery
{
    public string BuildLoopbackProxyServer(int mixedPort)
    {
        return proxyRecovery.BuildLoopbackProxyServer(mixedPort);
    }
}

internal sealed class NetworkTakeoverReadinessAdapter(MihomoControllerClient controller)
    : INetworkTakeoverReadiness
{
    public Task<bool> MatchesRuntimeConfigurationAsync(
        RuntimeConfigurationActivationPlan plan,
        long generation,
        string configurationHash,
        MihomoServiceStatus observedServiceStatus,
        CancellationToken cancellationToken)
    {
        if (plan.TunEnabled)
        {
            if (observedServiceStatus.ServiceSessionId is not Guid serviceSessionId
                || serviceSessionId == Guid.Empty
                || !observedServiceStatus.IsReady
                || observedServiceStatus.ActiveGeneration != generation
                || !StringComparer.Ordinal.Equals(
                    observedServiceStatus.ActiveConfigurationHash,
                    configurationHash))
            {
                return Task.FromResult(false);
            }

            MihomoServiceIpcControllerBinding expectedRuntime = new()
            {
                ServiceSessionId = serviceSessionId,
                Generation = generation,
                ConfigurationHash = configurationHash,
            };
            return controller.MatchesServiceRuntimeConfigurationAsync(
                plan,
                expectedRuntime,
                cancellationToken);
        }

        return controller.MatchesRuntimeConfigurationAsync(plan, cancellationToken);
    }
}
