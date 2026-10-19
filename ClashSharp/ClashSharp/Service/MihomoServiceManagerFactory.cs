using ClashSharp.Infrastructure.Processes;

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
    /// <returns>A service manager wired to SCM observation, authenticated IPC, and localization resources.</returns>
    public static MihomoServiceManager CreateDefault()
    {
        var connection = LoadConnection();
        return new MihomoServiceManager(
            new WindowsProcessRunner(),
            connection.Endpoint,
            connection.Client,
            LocalizationService.Instance.GetString,
            LoadConnection);
    }

    private static (MihomoServiceIpcEndpoint Endpoint, IMihomoServiceIpcClient Client) LoadConnection()
    {
        MihomoServiceIpcEndpoint endpoint = MihomoServiceIpcEndpoint.LoadForCurrentUser();
        return (endpoint, new NamedPipeMihomoServiceIpcClient(
            endpoint.PipeName,
            new WindowsMihomoServicePipeServerIdentityVerifier(MihomoServiceManager.ServiceName)));
    }
}
