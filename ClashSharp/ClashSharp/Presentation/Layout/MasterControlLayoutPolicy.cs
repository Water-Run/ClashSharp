using System;

namespace ClashSharp.Presentation.Layout;

/// <summary>Resolved visual layout for the master-control hero and mode selector.</summary>
/// <param name="IsSideBySide">Whether the hero and mode selector share one row.</param>
/// <param name="ModeColumnWidth">Width reserved for the mode selector when side by side.</param>
internal readonly record struct MasterControlLayout(
    bool IsSideBySide,
    double ModeColumnWidth);

/// <summary>Window-bounded dimensions and preferred side for the hero display editor.</summary>
internal readonly record struct MasterControlFlyoutLayout(double ContentWidth, double ListMaxHeight, bool OpensAbove, bool AlignRight);

/// <summary>Calculates the master-control layout without depending on WinUI controls.</summary>
/// <remarks>
/// Invariants: Invalid and narrow widths resolve to the vertically stacked safe layout.
/// Thread safety: Stateless and thread-safe.
/// Side effects: None.
/// </remarks>
internal static class MasterControlLayoutPolicy
{
    private const double SideBySideMinimumWidth = 620;
    private const double MinimumModeColumnWidth = 280;
    private const double MaximumModeColumnWidth = 360;
    private const double ModeColumnWidthRatio = 0.42;

    /// <summary>Fits the editor beside its actual anchor while reserving the title bar and measured footer.</summary>
    /// <remarks>All dimensions are DIPs. Presenter padding and a gap from each window edge remain outside the scrolling fields.</remarks>
    public static MasterControlFlyoutLayout ResolveFlyout(
        double viewportWidth, double viewportHeight, double anchorTop, double anchorBottom, double contentChromeHeight,
        double anchorLeft = 0, double anchorRight = 0, bool preferRightAlignment = false)
    {
        double width = double.IsFinite(viewportWidth) && viewportWidth > 0 ? viewportWidth : 800;
        double height = double.IsFinite(viewportHeight) && viewportHeight > 0 ? viewportHeight : 600;
        double top = Math.Clamp(double.IsFinite(anchorTop) ? anchorTop : 0, 0, height);
        double bottom = Math.Clamp(double.IsFinite(anchorBottom) ? anchorBottom : top, top, height);
        double chrome = double.IsFinite(contentChromeHeight) ? Math.Max(0, contentChromeHeight) : 0;
        double left = Math.Clamp(double.IsFinite(anchorLeft) ? anchorLeft : 0, 0, width);
        double right = Math.Clamp(double.IsFinite(anchorRight) ? anchorRight : left, left, width);
        double sideWidth = Math.Max(width - left - 16, right - 16);
        double contentWidth = Math.Max(1, Math.Min(480, Math.Min(width - 64, sideWidth - 24)));
        bool fitsLeft = left + contentWidth + 24 <= width - 16;
        bool fitsRight = right - contentWidth - 24 >= 16;
        bool alignRight = preferRightAlignment ? fitsRight : !fitsLeft && fitsRight;
        double above = Math.Max(0, top - 60);
        double below = Math.Max(0, height - bottom - 16);
        bool opensAbove = above > below;
        double span = opensAbove ? above : below;
        return new(contentWidth, Math.Max(1, Math.Min(420, span - chrome - 24)), opensAbove, alignRight);
    }

    /// <summary>Resolves a compact layout for the available content width.</summary>
    /// <param name="contentWidth">Usable width after scroll-view padding and scrollbar allowance.</param>
    /// <returns>A deterministic layout value.</returns>
    public static MasterControlLayout Resolve(double contentWidth)
    {
        if (!double.IsFinite(contentWidth) || contentWidth < SideBySideMinimumWidth)
        {
            return new MasterControlLayout(
                IsSideBySide: false,
                ModeColumnWidth: 0);
        }

        return new MasterControlLayout(
            IsSideBySide: true,
            ModeColumnWidth: Math.Clamp(
                contentWidth * ModeColumnWidthRatio,
                MinimumModeColumnWidth,
                MaximumModeColumnWidth));
    }
}
