using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class MasterHeroStatusSelectionGateTests
{
    [Fact]
    public async Task PendingSelection_BlocksBindingFeedbackUntilItsWriteCompletes()
    {
        MasterHeroStatusSelectionGate gate = new();
        TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int writes = 0;
        Task<bool> first = gate.TryApplySelectionAsync(0, MasterHeroStatusItemKind.Latency, async (_, _) =>
        {
            writes++;
            await release.Task;
            Assert.False(await gate.TryApplySelectionAsync(1, MasterHeroStatusItemKind.CurrentNode, (_, _) =>
            {
                writes++;
                return Task.CompletedTask;
            }));
        });
        try
        {
            Assert.False(await gate.TryApplySelectionAsync(1, MasterHeroStatusItemKind.CurrentNode, (_, _) => Task.CompletedTask));
        }
        finally { release.TrySetResult(); }
        Assert.True(await first);
        Assert.Equal(1, writes);
        Assert.True(await gate.TryApplySelectionAsync(1, MasterHeroStatusItemKind.CurrentNode, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public async Task FailedAsyncReset_ReleasesTheSelectionGuard()
    {
        MasterHeroStatusSelectionGate gate = new();
        await Assert.ThrowsAsync<IOException>(() => gate.RunProgrammaticUpdateAsync(() => Task.FromException(new IOException("Reset failed"))));

        Assert.True(await gate.TryApplySelectionAsync(0, MasterHeroStatusItemKind.Latency, (_, _) => Task.CompletedTask));
    }

    [Fact]
    public void ResetAndFlyoutRebuild_DoNotReenterLayoutWrites()
    {
        MasterHeroStatusSelectionGate gate = new();
        int layoutWrites = 0;
        bool resetRan = false;
        bool rebuildRan = false;

        gate.RunProgrammaticUpdate(() =>
        {
            resetRan = true;
            Assert.False(gate.TryApplySelection(
                0,
                MasterHeroStatusItemKind.CoreStatus,
                (_, _) => layoutWrites++));

            rebuildRan = true;
            Assert.False(gate.TryApplySelection(
                1,
                MasterHeroStatusItemKind.CurrentNode,
                (_, _) => layoutWrites++));
        });

        Assert.True(resetRan);
        Assert.True(rebuildRan);
        Assert.Equal(0, layoutWrites);
        Assert.True(gate.TryApplySelection(
            2,
            MasterHeroStatusItemKind.ActiveConnections,
            (_, _) => layoutWrites++));
        Assert.Equal(1, layoutWrites);
    }

    [Fact]
    public void SelectionWrite_DoesNotReenterWhileViewModelRaisesSelectionChanges()
    {
        MasterHeroStatusSelectionGate gate = new();
        int layoutWrites = 0;

        Assert.True(gate.TryApplySelection(
            0,
            MasterHeroStatusItemKind.CoreStatus,
            (_, _) =>
            {
                layoutWrites++;
                Assert.False(gate.TryApplySelection(
                    1,
                    MasterHeroStatusItemKind.CurrentNode,
                    (_, _) => layoutWrites++));
            }));

        Assert.Equal(1, layoutWrites);
    }
}
