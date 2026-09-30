using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Settings;
using ClashSharp.ApplicationModel.Supervision;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Resolves sampling health and complete operations from the current owned runtime.</summary>
internal sealed class GenerationSamplingRuntime(DataGenerationManager generations)
    : GenerationRuntimeParticipant("connection-sampling", generations, runtime => runtime.Sampling), IConnectionSamplingRuntime
{
    public SupervisorHealth Health => Generations.ReadSnapshot<AppDataGenerationRuntime, SupervisorHealth>((runtime, _) => runtime.Sampling.Health);
    public bool IsRunning => Generations.ReadSnapshot<AppDataGenerationRuntime, bool>((runtime, _) => runtime.Sampling.IsRunning);
    public Task RestartFromSettingsAsync(CancellationToken cancellationToken) =>
        Generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, token) => runtime.Sampling.RestartFromSettingsAsync(token), cancellationToken);
    public Task FlushAsync(CancellationToken cancellationToken) =>
        Generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, token) => runtime.Sampling.FlushAsync(token), cancellationToken);
    public Task<ConnectionSamplingSettings> ReadConfigurationAsync(CancellationToken cancellationToken) =>
        Generations.ExecuteAsync<AppDataGenerationRuntime, ConnectionSamplingSettings>((runtime, _, token) => runtime.Sampling.ReadConfigurationAsync(token), cancellationToken);
}
