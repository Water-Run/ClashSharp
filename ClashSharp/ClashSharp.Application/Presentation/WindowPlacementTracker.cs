namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Retains normal bounds and the last visible, non-minimized mode without performing I/O.</summary>
public sealed class WindowPlacementTracker
{
    /// <summary>Gets the latest usable placement.</summary>
    public WindowPlacementState? Current { get; private set; }

    /// <summary>Seeds the resolved normal bounds before a programmatic maximize operation.</summary>
    public void Seed(WindowPlacementState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        state.Validate();
        Current = state;
    }

    /// <summary>Observes physical geometry; hidden and minimized states retain the previous record.</summary>
    public void Observe(WindowPlacementMonitor monitor, WindowPlacementBounds bounds, uint dpi,
        bool visible, bool minimized, bool maximized)
    {
        if (!visible || minimized) { return; }
        ArgumentNullException.ThrowIfNull(monitor);
        ArgumentNullException.ThrowIfNull(bounds);
        monitor.WorkArea.Validate();
        bounds.Validate();
        WindowPlacementBounds normal = maximized
            ? Current is { } saved ? WindowPlacementPolicy.Resolve(saved, monitor, dpi) : WindowPlacementPolicy.DefaultBounds(monitor, dpi)
            : bounds;
        var state = new WindowPlacementState(WindowPlacementState.CurrentSchema, monitor.Name, normal, monitor.WorkArea, dpi, maximized);
        state.Validate();
        Current = state;
    }
}
