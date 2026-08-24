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
        MihomoServiceIpcEndpoint endpoint = MihomoServiceIpcEndpoint.LoadForCurrentUser();
        return new MihomoServiceManager(
            new WindowsProcessRunner(),
            endpoint,
            new NamedPipeMihomoServiceIpcClient(
                endpoint.PipeName,
                new WindowsMihomoServicePipeServerIdentityVerifier(
                    MihomoServiceManager.ServiceName)),
            LocalizationService.Instance.GetString);
    }
}
