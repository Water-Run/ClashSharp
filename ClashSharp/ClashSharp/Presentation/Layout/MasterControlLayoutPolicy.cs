using System;

namespace ClashSharp.Presentation.Layout;

/// <summary>Resolved visual layout for the master-control hero and mode selector.</summary>
/// <param name="IsSideBySide">Whether the hero and mode selector share one row.</param>
/// <param name="ModeColumnWidth">Width reserved for the mode selector when side by side.</param>
internal readonly record struct MasterControlLayout(
    bool IsSideBySide,
    double ModeColumnWidth);

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
