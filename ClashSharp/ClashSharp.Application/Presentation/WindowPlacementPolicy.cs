namespace ClashSharp.ApplicationModel.Presentation;

/// <summary>Resolves saved geometry without requiring a Windows desktop or trusting old display topology.</summary>
public static class WindowPlacementPolicy
{
    /// <summary>Minimum layout width in device-independent pixels when the display can accommodate it.</summary>
    public const int MinimumWidth = 800;
    /// <summary>Minimum layout height in device-independent pixels when the display can accommodate it.</summary>
    public const int MinimumHeight = 600;

    /// <summary>Chooses the previous monitor when present, otherwise the closest usable monitor.</summary>
    public static WindowPlacementMonitor SelectMonitor(WindowPlacementState state, IReadOnlyList<WindowPlacementMonitor> monitors)
    {
        state.Validate();
        ArgumentNullException.ThrowIfNull(monitors);
        if (monitors.Count == 0) { throw new ArgumentException("No display is available.", nameof(monitors)); }
        foreach (WindowPlacementMonitor monitor in monitors)
        {
            ArgumentNullException.ThrowIfNull(monitor);
            monitor.WorkArea.Validate();
            if (string.IsNullOrWhiteSpace(monitor.Name)) { throw new ArgumentException("Invalid monitor name.", nameof(monitors)); }
        }
        return monitors.FirstOrDefault(monitor => string.Equals(monitor.Name, state.MonitorName, StringComparison.OrdinalIgnoreCase))
            ?? monitors.OrderBy(monitor => DistanceSquared(state.NormalBounds, monitor.WorkArea)).First();
    }

    /// <summary>Scales logical size and offsets, then keeps the complete window in the current work area.</summary>
    public static WindowPlacementBounds Resolve(WindowPlacementState state, WindowPlacementMonitor monitor, uint dpi)
    {
        state.Validate();
        ArgumentNullException.ThrowIfNull(monitor);
        monitor.WorkArea.Validate();
        if (dpi is < 48 or > 768) { throw new ArgumentOutOfRangeException(nameof(dpi)); }
        WindowPlacementBounds area = monitor.WorkArea;
        double ratio = (double)dpi / state.Dpi;
        int minimumWidth = Math.Min(area.Width, Scale(MinimumWidth, (double)dpi / 96));
        int minimumHeight = Math.Min(area.Height, Scale(MinimumHeight, (double)dpi / 96));
        int width = Math.Clamp(Scale(state.NormalBounds.Width, ratio), minimumWidth, area.Width);
        int height = Math.Clamp(Scale(state.NormalBounds.Height, ratio), minimumHeight, area.Height);
        bool sameMonitor = string.Equals(state.MonitorName, monitor.Name, StringComparison.OrdinalIgnoreCase);
        int x = sameMonitor ? area.X + Scale(state.NormalBounds.X - state.WorkArea.X, ratio) : area.X + (area.Width - width) / 2;
        int y = sameMonitor ? area.Y + Scale(state.NormalBounds.Y - state.WorkArea.Y, ratio) : area.Y + (area.Height - height) / 2;
        return new(Math.Clamp(x, area.X, area.X + area.Width - width),
            Math.Clamp(y, area.Y, area.Y + area.Height - height), width, height);
    }

    /// <summary>Supplies restored bounds when a window first appears already maximized.</summary>
    public static WindowPlacementBounds DefaultBounds(WindowPlacementMonitor monitor, uint dpi)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var state = new WindowPlacementState(WindowPlacementState.CurrentSchema, monitor.Name,
            monitor.WorkArea, monitor.WorkArea, dpi, false);
        WindowPlacementBounds area = monitor.WorkArea;
        var desired = new WindowPlacementBounds(area.X + area.Width / 10, area.Y + area.Height / 10,
            Math.Max(1, area.Width * 4 / 5), Math.Max(1, area.Height * 4 / 5));
        return Resolve(state with { NormalBounds = desired }, monitor, dpi);
    }

    private static int Scale(int value, double ratio) => checked((int)Math.Round(value * ratio, MidpointRounding.AwayFromZero));
    private static double DistanceSquared(WindowPlacementBounds window, WindowPlacementBounds area)
    {
        double x = window.X + window.Width / 2d;
        double y = window.Y + window.Height / 2d;
        double dx = x - Math.Clamp(x, area.X, area.X + area.Width);
        double dy = y - Math.Clamp(y, area.Y, area.Y + area.Height);
        return dx * dx + dy * dy;
    }
}
