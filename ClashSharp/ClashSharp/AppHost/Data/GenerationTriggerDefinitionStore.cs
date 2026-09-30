using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Triggers;
using ClashSharp.Model.Triggers;

namespace ClashSharp.Hosting.Data;

/// <summary>Keeps trigger editing and cached summaries on the current generation.</summary>
internal sealed class GenerationTriggerDefinitionStore(DataGenerationManager generations) : ITriggerDefinitionStore
{
    public TriggerDefinitionCatalog Current => generations.ReadSnapshot<AppDataGenerationRuntime, TriggerDefinitionCatalog>((runtime, _) => runtime.Definitions.Current);
    public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(CancellationToken cancellationToken) =>
        generations.ExecuteAsync<AppDataGenerationRuntime, TriggerPersistenceResult<TriggerDefinitionCatalog>>(
            (runtime, _, token) => runtime.Definitions.ReadAsync(token), cancellationToken);
    public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(long expectedGeneration,
        IReadOnlyList<TriggerTaskDefinition> definitions, CancellationToken cancellationToken) =>
        generations.ExecuteAsync<AppDataGenerationRuntime, TriggerPersistenceResult<TriggerDefinitionCatalog>>(
            (runtime, _, token) => runtime.Definitions.ReplaceAsync(expectedGeneration, definitions, token), cancellationToken);
}
