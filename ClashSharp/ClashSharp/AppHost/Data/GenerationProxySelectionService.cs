using System;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Resolves node choices from the active data scope and pins every complete selection or restoration.</summary>
internal sealed class GenerationProxySelectionService(
    Func<DataGenerationManager> getGenerations, Func<IProxySelectionService>? startupRecovery = null) : IProxySelectionService
{
    public Task SelectAsync(string groupName, string proxyName, CancellationToken cancellationToken) =>
        ExecuteAsync((service, token) => service.SelectAsync(groupName, proxyName, token), cancellationToken);
    public Task RestoreAsync(RuntimeConfigurationActivationPlan plan, CancellationToken cancellationToken) =>
        ExecuteAsync((service, token) => service.RestoreAsync(plan, token), cancellationToken);

    private Task ExecuteAsync(Func<IProxySelectionService, CancellationToken, Task> operation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        DataGenerationManager generations = getGenerations();
        if (generations.IsAwaitingInitialization && startupRecovery is not null) { return operation(startupRecovery(), cancellationToken); }
        return generations.ExecuteAsync<IProxySelectionService>((service, _, token) => operation(service, token), cancellationToken);
    }
}
