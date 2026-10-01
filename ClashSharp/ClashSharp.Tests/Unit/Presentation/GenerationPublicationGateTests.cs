extern alias ClashSharpUi;
using Gate = ClashSharpUi::ClashSharp.Hosting.Data.GenerationPublicationGate;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class GenerationPublicationGateTests
{
    [Fact]
    public async Task ReplacementPublication_WaitersFollowEachHoldAndPublishCycle()
    {
        Gate gate = new();
        Task first = gate.WaitAsync(CancellationToken.None);
        Assert.False(first.IsCompleted);
        Assert.False(gate.Hold());
        gate.Publish();
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(gate.IsPublished);

        Assert.True(gate.Hold());
        Task second = gate.WaitAsync(CancellationToken.None);
        Assert.False(second.IsCompleted);
        gate.Publish();
        await second.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReplacementPublication_CancellingOneWaiterDoesNotReleaseOrCancelAnother()
    {
        Gate gate = new();
        using CancellationTokenSource cancellation = new();
        Task canceled = gate.WaitAsync(cancellation.Token);
        Task retained = gate.WaitAsync(CancellationToken.None);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        Assert.False(retained.IsCompleted);
        Assert.False(gate.IsPublished);
        gate.Publish();
        await retained.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ReplacementPublication_AnAlreadyCanceledOwnerCannotStartThroughAnOpenGate()
    {
        Gate gate = new();
        gate.Publish();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => gate.WaitAsync(new CancellationToken(true)));
    }
}
