using ClashSharp.Model;
using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

public sealed class MasterHeroStatusSelectionGateTests
{
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
