using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Hosting.Settings;

namespace ClashSharp.Hosting.Data;

/// <summary>Retains complete admitted network participant operations in the current generation.</summary>
internal sealed class GenerationNetworkSettingsRuntime(DataGenerationManager generations) : INetworkSettingsRuntime
{
    public Task<NetworkSettingsConfiguration> ReadConfigurationAsync(CancellationToken cancellationToken) =>
        generations.ExecuteAsync<AppDataGenerationRuntime, NetworkSettingsConfiguration>(
            (runtime, _, token) => runtime.Network.ReadConfigurationAsync(token), cancellationToken);
    public Task RecoverConfigurationAsync(CancellationToken cancellationToken) =>
        generations.ExecuteAsync<AppDataGenerationRuntime>(
            (runtime, _, token) => runtime.Network.RecoverConfigurationAsync(token), cancellationToken);
    public Task ApplyConfigurationAsync(NetworkSettingsConfiguration configuration, CancellationToken cancellationToken) =>
        generations.ExecuteAsync<AppDataGenerationRuntime>(
            (runtime, _, token) => runtime.Network.ApplyConfigurationAsync(configuration, token), cancellationToken);
}
