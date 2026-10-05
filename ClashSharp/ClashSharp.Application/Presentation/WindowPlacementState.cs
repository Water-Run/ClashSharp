namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>A bounded rectangle in physical virtual-screen coordinates.</summary>
/// <param name="X">Left coordinate, including negative monitor origins.</param>
/// <param name="Y">Top coordinate, including negative monitor origins.</param>
/// <param name="Width">Positive physical width.</param>
/// <param name="Height">Positive physical height.</param>
public sealed record WindowPlacementBounds(int X, int Y, int Width, int Height)
{
    /// <summary>Rejects invalid or unreasonably large persisted geometry.</summary>
    public void Validate()
    {
        if (X is < -1_000_000 or > 1_000_000 || Y is < -1_000_000 or > 1_000_000
            || Width is < 1 or > 100_000 || Height is < 1 or > 100_000)
        {
            throw new ArgumentException("Invalid window placement bounds.");
        }
    }
}

/// <summary>A current monitor and its usable screen rectangle, excluding taskbars.</summary>
/// <param name="Name">Windows monitor device name.</param>
/// <param name="WorkArea">Usable bounds in screen coordinates.</param>
public sealed record WindowPlacementMonitor(string Name, WindowPlacementBounds WorkArea);

/// <summary>Device-local window state, independent of transferable configuration preferences.</summary>
/// <param name="Schema">Document schema version.</param>
/// <param name="MonitorName">Last monitor device name.</param>
/// <param name="NormalBounds">Last restored bounds; never minimized or maximized screen bounds.</param>
/// <param name="WorkArea">Work area at capture time.</param>
/// <param name="Dpi">Effective monitor DPI at capture time.</param>
/// <param name="IsMaximized">Last non-minimized presentation mode.</param>
public sealed record WindowPlacementState(int Schema, string MonitorName, WindowPlacementBounds NormalBounds,
    WindowPlacementBounds WorkArea, uint Dpi, bool IsMaximized)
{
    /// <summary>The supported document schema.</summary>
    public const int CurrentSchema = 1;

    /// <summary>Validates the complete bounded state.</summary>
    public void Validate()
    {
        if (Schema != CurrentSchema || string.IsNullOrWhiteSpace(MonitorName) || MonitorName.Length > 128
            || MonitorName.Any(char.IsControl) || Dpi is < 48 or > 768)
        {
            throw new ArgumentException("Invalid window placement state.");
        }
        ArgumentNullException.ThrowIfNull(NormalBounds);
        ArgumentNullException.ThrowIfNull(WorkArea);
        NormalBounds.Validate();
        WorkArea.Validate();
    }
}

/// <summary>Reads and atomically saves the one device-local window-state document.</summary>
public interface IWindowPlacementStore
{
    /// <summary>Reads saved placement, or null when no placement has been saved.</summary>
    Task<WindowPlacementState?> LoadAsync(CancellationToken cancellationToken);

    /// <summary>Saves one complete validated placement.</summary>
    Task SaveAsync(WindowPlacementState state, CancellationToken cancellationToken);
}

/// <summary>UI-thread-only window operations without exposing platform handles.</summary>
public interface IWindowPlacementView
{
    /// <summary>Observes the current visible state without persisting transient minimized bounds.</summary>
    void Observe();

    /// <summary>Returns the latest non-minimized state.</summary>
    WindowPlacementState? Capture();

    /// <summary>Restores valid placement within the current display topology.</summary>
    void Restore(WindowPlacementState state);

    /// <summary>Constrains the native minimum tracking size to the current usable display.</summary>
    (int Width, int Height) ConstrainMinimumSize(int width, int height);
}
