using System.Threading;
using System.Threading.Tasks;
using ClashSharp.ApplicationModel.Lifecycle;
using ClashSharp.Service;

namespace ClashSharp.Hosting.Compatibility;

/// <summary>Temporary awaited lifecycle adapter for the legacy trigger scheduler.</summary>
internal sealed class LegacyTriggerRuntimeParticipant(TriggerService triggers) : IRuntimeParticipant
{
    public string Name => "trigger-supervisor";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        triggers.Start();
        return Task.CompletedTask;
    }

    public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
    {
        bool wasRunning = triggers.IsAcceptingRuntimeWork;
        try
        {
            await triggers.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            return new QuiescedState(wasRunning);
        }
        catch
        {
            if (wasRunning)
            {
                triggers.Start();
            }

            throw;
        }
    }

    public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (priorState.WasRunning)
        {
            triggers.Start();
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return triggers.QuiesceAsync(cancellationToken);
    }
}

/// <summary>Temporary awaited lifecycle adapter for the legacy connection sampling loop.</summary>
internal sealed class LegacyConnectionSamplingRuntimeParticipant(
    ConnectionSamplingService sampling) : IRuntimeParticipant
{
    public string Name => "connection-sampling";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sampling.StartIfEnabled();
        return Task.CompletedTask;
    }

    public async Task<QuiescedState> QuiesceAsync(CancellationToken cancellationToken)
    {
        bool wasRunning = sampling.IsRunning;
        try
        {
            wasRunning = await sampling.QuiesceAsync(cancellationToken).ConfigureAwait(false);
            return new QuiescedState(wasRunning);
        }
        catch
        {
            sampling.ResumeAfterQuiescence(wasRunning);
            throw;
        }
    }

    public Task ResumeAsync(QuiescedState priorState, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        sampling.ResumeAfterQuiescence(priorState.WasRunning);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return sampling.StopAsync(cancellationToken);
    }
}
