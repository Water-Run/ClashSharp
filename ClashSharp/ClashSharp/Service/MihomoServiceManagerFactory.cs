using System;
using System.IO;
using ClashSharp.Infrastructure.Processes;
using ClashSharp.Model;

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
    /// <returns>A service manager wired to sc.exe, application paths, core state, and localization resources.</returns>
    public static MihomoServiceManager CreateDefault()
    {
        MihomoServiceIpcEndpoint endpoint = MihomoServiceIpcEndpoint.LoadForCurrentUser();
        return new MihomoServiceManager(
            new WindowsProcessRunner(),
            new MihomoServiceDeploymentContext(
                CoreConfigurationService.Instance,
                MihomoCoreService.Instance),
            new WindowsMihomoServiceBinaryTrustValidator(
                MihomoServicePackageTrust.ResolveCurrentPackageInstallRoot()),
            endpoint,
            new NamedPipeMihomoServiceIpcClient(
                endpoint.PipeName,
                new WindowsMihomoServicePipeServerIdentityVerifier(
                    MihomoServiceManager.ServiceName)),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class MihomoServiceDeploymentContext(
    CoreConfigurationService configuration,
    MihomoCoreService core) : IMihomoServiceDeploymentContext
{
    public string? ResolveServiceHostPath()
    {
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "Binaries", "Service", "ClashSharp.MihomoService.exe"),
            Path.Combine(AppContext.BaseDirectory, "ClashSharp.MihomoService.exe"),
        ];

        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public string MihomoBinaryPath => core.BinaryPath;

    public CoreConfigurationState EnsureServiceConfiguration()
    {
        // Deployment only needs the stable path embedded in SCM's binPath. The
        // admitted runtime transaction creates/promotes the file before starting
        // the service; deployment must never publish a live generation itself.
        return configuration.GetState();
    }
}
