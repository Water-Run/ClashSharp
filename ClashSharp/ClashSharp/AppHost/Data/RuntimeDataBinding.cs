using System;
using System.Threading;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Owns the lifetime of process-wide native repository ports without opening data or starting services.</summary>
internal sealed class RuntimeDataBinding : IDisposable
{
    private int _disposed;
    private readonly IDisposable _binding;
    public RuntimeDataBinding(DataGenerationManager generations)
    {
        Generations = generations ?? throw new ArgumentNullException(nameof(generations));
        _binding = RuntimeDataServices.Bind(new GenerationCoreConfigurationStore(() => generations, () => CoreConfigurationService.Instance),
            new GenerationLogStorage(() => generations, () => LogStorageService.Instance),
            new GenerationProxySelectionService(() => generations, () => ProxySelectionService.Instance),
            token => generations.IsAwaitingInitialization
                ? ConnectionSamplingService.GetCreatedInstance()?.FlushAsync(token) ?? System.Threading.Tasks.Task.CompletedTask
                : generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, ownedToken) => runtime.Sampling.FlushAsync(ownedToken), token));
    }
    internal DataGenerationManager Generations { get; }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) { _binding.Dispose(); }
    }
}
