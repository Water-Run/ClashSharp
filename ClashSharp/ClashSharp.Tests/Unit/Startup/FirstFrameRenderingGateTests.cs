extern alias ClashSharpUi;

using FirstFrameRenderingGate =
    ClashSharpUi::ClashSharp.Hosting.Startup.FirstFrameRenderingGate;

namespace ClashSharp.Tests.Unit.Startup;

/// <summary>Verifies primary host construction can wait on a real rendering signal within a bound.</summary>
public sealed class FirstFrameRenderingGateTests
{
    [Fact]
    public async Task WaitAsync_BeforeRendering_RemainsPending()
    {
        FirstFrameRenderingGate gate = new();

        Task wait = gate.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(wait.IsCompleted);
        gate.SignalRendering();
        await wait;
    }

    [Fact]
    public async Task WaitAsync_RenderingAlreadyObserved_CompletesImmediately()
    {
        FirstFrameRenderingGate gate = new();
        gate.SignalRendering();

        await gate.WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Fact]
    public async Task WaitAsync_RenderingNeverObserved_TimesOut()
    {
        FirstFrameRenderingGate gate = new();

        await Assert.ThrowsAsync<TimeoutException>(
            () => gate.WaitAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
    }
}
