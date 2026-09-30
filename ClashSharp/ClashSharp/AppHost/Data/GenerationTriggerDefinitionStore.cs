using System;
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
    public TriggerDefinitionCatalog Current => generations.ReadSnapshot<AppDataGenerationRuntime, TriggerDefinitionCatalog>(
        (runtime, descriptor) => Bind(runtime.Definitions.Current, descriptor.GenerationId));
    public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReadAsync(CancellationToken cancellationToken) =>
        generations.ExecuteAsync<AppDataGenerationRuntime, TriggerPersistenceResult<TriggerDefinitionCatalog>>(
            async (runtime, descriptor, token) => Bind(await runtime.Definitions.ReadAsync(token).ConfigureAwait(false), descriptor.GenerationId), cancellationToken);
    public Task<TriggerPersistenceResult<TriggerDefinitionCatalog>> ReplaceAsync(TriggerCatalogVersion expectedVersion,
        IReadOnlyList<TriggerTaskDefinition> definitions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definitions);
        TriggerTaskDefinition[] snapshot = [.. definitions];
        return generations.ExecuteAsync<AppDataGenerationRuntime, TriggerPersistenceResult<TriggerDefinitionCatalog>>(
            async (runtime, descriptor, token) => expectedVersion.DataGenerationId != descriptor.GenerationId
                ? TriggerPersistenceResult.Conflict<TriggerDefinitionCatalog>()
                : Bind(await runtime.Definitions.ReplaceAsync(expectedVersion.Generation, snapshot, token).ConfigureAwait(false), descriptor.GenerationId), cancellationToken);
    }

    private static TriggerDefinitionCatalog Bind(TriggerDefinitionCatalog catalog, Guid id) =>
        new(catalog.Generation, catalog.Tasks, catalog.Diagnostics, id);

    private static TriggerPersistenceResult<TriggerDefinitionCatalog> Bind(TriggerPersistenceResult<TriggerDefinitionCatalog> result, Guid id) =>
        result.IsSucceeded && result.Value is { } catalog ? TriggerPersistenceResult.Succeeded(Bind(catalog, id)) : result;
}
