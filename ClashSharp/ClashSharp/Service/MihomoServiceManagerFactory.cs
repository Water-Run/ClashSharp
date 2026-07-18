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
    /// <returns>A service manager wired to sc.exe, application paths, settings, and localization resources.</returns>
    public static MihomoServiceManager CreateDefault()
    {
        return new MihomoServiceManager(
            new WindowsProcessRunner(),
            new MihomoServiceDeploymentContext(
                AppSettingsService.Instance,
                CoreConfigurationService.Instance,
                MihomoCoreService.Instance),
            LocalizationService.Instance.GetString);
    }
}

internal sealed class MihomoServiceDeploymentContext(
    AppSettingsService settings,
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

    public CoreConfigurationState EnsureTransparentProxyConfiguration()
    {
        return configuration.EnsureConfiguration(settings.CurrentMode, transparentProxyEnabled: true);
    }
}
