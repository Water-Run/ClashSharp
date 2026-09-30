using System;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Data;
using ClashSharp.ApplicationModel.Lifecycle;

namespace ClashSharp.Hosting.Data;

/// <summary>Retains each complete lifecycle operation in its generation and rejects a resume token from another generation.</summary>
internal class GenerationRuntimeParticipant(
    string name, DataGenerationManager generations, Func<AppDataGenerationRuntime, IRuntimeParticipant> select) : IRuntimeParticipant
{
    private readonly ConditionalWeakTable<QuiescedState, ResumeToken> _resumes = new();
    protected DataGenerationManager Generations { get; } = generations ?? throw new ArgumentNullException(nameof(generations));
    public string Name { get; } = name;
    public Task StartAsync(CancellationToken cancellationToken) =>
        Generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, token) => select(runtime).StartAsync(token), cancellationToken);
    public Task StopAsync(CancellationToken cancellationToken) =>
        Generations.ExecuteAsync<AppDataGenerationRuntime>((runtime, _, token) => select(runtime).StopAsync(token), cancellationToken);
    public Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken) =>
        Generations.ExecuteAsync<AppDataGenerationRuntime, QuiescedState>(async (runtime, generation, token) =>
        {
            QuiescedState prior = await select(runtime).QuiesceAsync(token).ConfigureAwait(false);
            QuiescedState result = new(prior.WasRunning);
            _resumes.Add(result, new(generation, prior));
            return result;
        }, cancellationToken);
    public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken) =>
        Generations.ExecuteAsync<AppDataGenerationRuntime>(async (runtime, generation, token) =>
        {
            if (!_resumes.TryGetValue(priorState, out ResumeToken? resume) || !resume.Generation.IsSameGeneration(generation))
            {
                throw new InvalidOperationException("The runtime resume token belongs to another generation or participant.");
            }
            await select(runtime).ResumeAsync(resume.State, token).ConfigureAwait(false);
            _resumes.Remove(priorState);
        }, cancellationToken);

    private sealed record ResumeToken(DataGenerationDescriptor Generation, QuiescedState State);
}
