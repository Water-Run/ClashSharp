using ClashSharp.ApplicationModel.Diagnostics;

namespace ClashSharp.ApplicationModel.Lifecycle;

/// <summary>Records successful pauses so a failed destructive transition can restore them in reverse order.</summary>
internal sealed class QuiescenceSession
{
    private readonly List<PausedParticipant> _paused = [];

    public async Task QuiesceAsync(
        IReadOnlyList<IRuntimeParticipant> participants,
        CancellationToken cancellationToken)
    {
        foreach (IRuntimeParticipant participant in participants)
        {
            QuiescedState priorState = await participant
                .QuiesceAsync(cancellationToken)
                .ConfigureAwait(false);
            _paused.Add(new PausedParticipant(participant, priorState));
        }
    }

    public async Task<IReadOnlyList<string>> ResumeAsync(CancellationToken cancellationToken)
    {
        List<string> failures = [];
        List<Exception> processFatalFailures = [];
        for (int index = _paused.Count - 1; index >= 0; index--)
        {
            PausedParticipant paused = _paused[index];
            try
            {
                await paused.Participant
                    .ResumeAsync(paused.PriorState, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception exception) when (ExceptionGraphClassifier.IsProcessFatal(exception))
            {
                processFatalFailures.Add(exception);
            }
            catch (Exception)
            {
                failures.Add(paused.Participant.Name);
            }
        }

        if (processFatalFailures.Count != 0)
        {
            throw new AggregateException(
                "One or more runtime participants failed fatally while resuming.",
                processFatalFailures);
        }

        return failures;
    }

    private sealed record PausedParticipant(
        IRuntimeParticipant Participant,
        QuiescedState PriorState);
}
