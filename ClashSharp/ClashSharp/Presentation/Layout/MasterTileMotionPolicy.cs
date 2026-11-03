using System;

namespace ClashSharp.Presentation.Layout;

/// <summary>Geometry and timing for the position-sensitive Metro tile interaction.</summary>
internal static class MasterTileMotionPolicy
{
    internal const double MaximumTiltDegrees = 17;
    internal const double MaximumDepression = 24;
    internal const double PickUpScale = 1.02;
    internal const double DragOpacity = 0.65;
    internal const int PressMilliseconds = 80;
    internal const int ReleaseMilliseconds = 160;

    internal readonly record struct PressPose(double RotationX, double RotationY, double Depth);

    // Mouse clicks have no duration threshold. A held left button becomes a drag only
    // after deliberate spatial movement; native context gestures own press-and-hold.
    internal static bool ShouldStartMouseDrag(double x, double y) =>
        double.IsFinite(x) && double.IsFinite(y) && x * x + y * y >= 64;

    // Normalize against the actual surface, rather than a fixed pixel pivot: the same
    // relative press has the same response at every DPI, card width and orientation.
    internal static PressPose GetPressPose(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width)
            || !double.IsFinite(height) || width <= 0 || height <= 0)
        {
            return default;
        }

        double horizontal = Math.Clamp(2 * x / width - 1, -1, 1);
        double vertical = Math.Clamp(2 * y / height - 1, -1, 1);
        double edge = Math.Max(Math.Abs(horizontal), Math.Abs(vertical));
        double depression = Math.Min(MaximumDepression, Math.Min(width, height) * 0.16);
        // PlaneProjection's positive X rotation depresses the bottom edge; its positive
        // Y rotation depresses the left edge. Press coordinates have a top-left origin.
        return new PressPose(vertical * MaximumTiltDegrees, -horizontal * MaximumTiltDegrees,
            -depression * (1 - edge));
    }
}
