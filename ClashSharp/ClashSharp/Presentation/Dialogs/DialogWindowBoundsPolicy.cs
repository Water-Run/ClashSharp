using System;

namespace ClashSharp.Presentation.Dialogs;

/// <summary>Fits a dialog to its monitor while using the owner only as a centering anchor.</summary>
internal static class DialogWindowBoundsPolicy
{
    internal readonly record struct Bounds(int X, int Y, int Width, int Height);

    internal static Bounds Place(Bounds workArea, Bounds owner, double scale, double width, double height)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0) { throw new ArgumentOutOfRangeException(nameof(workArea)); }
        if (!double.IsFinite(scale) || scale <= 0) { throw new ArgumentOutOfRangeException(nameof(scale)); }
        if (!double.IsFinite(width) || width <= 0) { throw new ArgumentOutOfRangeException(nameof(width)); }
        if (!double.IsFinite(height) || height <= 0) { throw new ArgumentOutOfRangeException(nameof(height)); }
        int marginX = (int)Math.Min(Math.Ceiling(16 * scale), (workArea.Width - 1) / 2);
        int marginY = (int)Math.Min(Math.Ceiling(16 * scale), (workArea.Height - 1) / 2);
        int physicalWidth = (int)Math.Clamp(Math.Ceiling(width * scale), 1, workArea.Width - 2 * marginX);
        int physicalHeight = (int)Math.Clamp(Math.Ceiling(height * scale), 1, workArea.Height - 2 * marginY);
        double centerX = owner.X + owner.Width / 2d;
        double centerY = owner.Y + owner.Height / 2d;
        int x = checked((int)Math.Clamp(Math.Round(centerX - physicalWidth / 2d),
            (double)workArea.X + marginX, (double)workArea.X + workArea.Width - marginX - physicalWidth));
        int y = checked((int)Math.Clamp(Math.Round(centerY - physicalHeight / 2d),
            (double)workArea.Y + marginY, (double)workArea.Y + workArea.Height - marginY - physicalHeight));
        return new(x, y, physicalWidth, physicalHeight);
    }
}
