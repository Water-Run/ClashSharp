using System;
using System.Threading;
using System.Threading.Tasks;

namespace ClashSharp.Hosting.Startup;

/// <summary>Represents the first compositor rendering signal for the primary startup shell.</summary>
/// <remarks>
/// The WinUI event remains owned by <c>App</c>; this gate only owns its completion state so the
/// rendering contract can be verified without constructing a window or starting a message loop.
/// </remarks>
internal sealed class FirstFrameRenderingGate
{
    private readonly TaskCompletionSource _rendered =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Signals that WinUI has entered a rendering pass after window activation.</summary>
    internal void SignalRendering()
    {
        _rendered.TrySetResult();
    }

    /// <summary>Waits within a finite bound for the first rendering signal.</summary>
    internal Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "The first-frame rendering wait must be finite and positive.");
        }

        return _rendered.Task.WaitAsync(timeout, cancellationToken);
    }
}
