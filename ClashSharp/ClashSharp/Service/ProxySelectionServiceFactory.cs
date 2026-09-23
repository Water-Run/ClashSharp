using System.IO;

namespace ClashSharp.Service;

internal sealed partial class ProxySelectionService
{
    internal static ProxySelectionService Instance { get; } = new(
        new ProxySelectionStore(Path.Combine(AppDataPathService.ResolveLocalDataDirectory(), "mihomo", "proxy-selections.json")),
        MihomoControllerClient.Instance.GetProxyGroupsAsync,
        MihomoControllerClient.Instance.SelectProxyAsync,
        CoreConfigurationService.Instance.ObserveRuntimeConfigurationIntegrity,
        LateBoundProfileCatalogMutationCoordinator.Instance);
}
