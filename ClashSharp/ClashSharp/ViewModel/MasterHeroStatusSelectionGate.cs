using System;
using System.Threading.Tasks;
using ClashSharp.Model;

namespace ClashSharp.ViewModel;

/// <summary>Prevents programmatic hero-status selection updates from re-entering layout writes.</summary>
internal sealed class MasterHeroStatusSelectionGate
{
    private int _updateDepth;

    public async Task<bool> TryApplySelectionAsync(
        int slotIndex,
        MasterHeroStatusItemKind kind,
        Func<int, MasterHeroStatusItemKind, Task> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (_updateDepth > 0) { return false; }
        await RunProgrammaticUpdateAsync(() => apply(slotIndex, kind));
        return true;
    }

    public async Task RunProgrammaticUpdateAsync(Func<Task> update)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            _updateDepth++;
            await update();
        }
        finally
        {
            _updateDepth--;
        }
    }

    public bool TryApplySelection(
        int slotIndex,
        MasterHeroStatusItemKind kind,
        Action<int, MasterHeroStatusItemKind> apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (_updateDepth > 0)
        {
            return false;
        }

        RunProgrammaticUpdate(() => apply(slotIndex, kind));
        return true;
    }

    public void RunProgrammaticUpdate(Action update)
    {
        ArgumentNullException.ThrowIfNull(update);
        try
        {
            _updateDepth++;
            update();
        }
        finally
        {
            _updateDepth--;
        }
    }
}
