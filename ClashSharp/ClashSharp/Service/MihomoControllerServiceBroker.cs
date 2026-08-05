using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Service;

/// <summary>Exposes only typed service-owned controller capabilities to the App runtime.</summary>
internal interface IMihomoControllerServiceBroker
{
    /// <summary>Returns the last conclusive owner observation without process I/O.</summary>
    MihomoServiceStatus GetLatestStatus();

    /// <summary>Observes both SCM state and the authenticated service-child snapshot.</summary>
    Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken);

    /// <summary>Executes one allowlisted capability against an exact service runtime.</summary>
    Task<MihomoServiceIpcResponse> SendAsync(
        MihomoServiceIpcCommand command,
        MihomoServiceIpcControllerBinding expectedRuntime,
        string? connectionId,
        MihomoServiceIpcProxySelection? proxySelection,
        MihomoServiceIpcRuntimeLogQuery? runtimeLogQuery,
        CancellationToken cancellationToken);

    /// <summary>Updates one typed provider against an exact service runtime.</summary>
    Task<MihomoServiceIpcResponse> UpdateProviderAsync(
        MihomoServiceIpcControllerBinding expectedRuntime,
        MihomoServiceIpcProviderUpdate providerUpdate,
        CancellationToken cancellationToken);
}

/// <summary>Adapts the production service manager without exposing arbitrary IPC requests.</summary>
internal sealed class MihomoControllerServiceBroker(MihomoServiceManager serviceManager)
    : IMihomoControllerServiceBroker
{
    private readonly MihomoServiceManager _serviceManager = serviceManager
        ?? throw new ArgumentNullException(nameof(serviceManager));

    public MihomoServiceStatus GetLatestStatus()
    {
        return _serviceManager.GetLatestStatus();
    }

    public Task<MihomoServiceStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        return _serviceManager.GetStatusAsync(cancellationToken);
    }

    public Task<MihomoServiceIpcResponse> SendAsync(
        MihomoServiceIpcCommand command,
        MihomoServiceIpcControllerBinding expectedRuntime,
        string? connectionId,
        MihomoServiceIpcProxySelection? proxySelection,
        MihomoServiceIpcRuntimeLogQuery? runtimeLogQuery,
        CancellationToken cancellationToken)
    {
        return _serviceManager.SendControllerIpcAsync(
            command,
            expectedRuntime,
            connectionId,
            proxySelection,
            runtimeLogQuery,
            cancellationToken);
    }

    public Task<MihomoServiceIpcResponse> UpdateProviderAsync(
        MihomoServiceIpcControllerBinding expectedRuntime,
        MihomoServiceIpcProviderUpdate providerUpdate,
        CancellationToken cancellationToken)
    {
        return _serviceManager.UpdateProviderIpcAsync(
            expectedRuntime,
            providerUpdate,
            cancellationToken);
    }
}
