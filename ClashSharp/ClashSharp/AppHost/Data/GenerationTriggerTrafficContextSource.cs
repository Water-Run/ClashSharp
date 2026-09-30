using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Data;

/// <summary>Keeps every rolling and all-time traffic query in one generation, including the complete asynchronous database read.</summary>
internal sealed class GenerationTriggerTrafficContextSource(DataGenerationManager generations) : ITriggerTrafficContextSource
{
    private readonly DataGenerationManager _generations = generations ?? throw new ArgumentNullException(nameof(generations));

    public Task<TriggerTrafficContextSnapshot> ReadAsync(
        IReadOnlyCollection<TimeSpan> rollingWindows,
        bool includeAllTimeTraffic,
        DateTimeOffset observedAt,
        CancellationToken cancellationToken)
    {
        return _generations.ExecuteAsync<ITriggerTrafficContextSource, TriggerTrafficContextSnapshot>(
            (source, _, token) => source.ReadAsync(rollingWindows, includeAllTimeTraffic, observedAt, token), cancellationToken);
    }
}
