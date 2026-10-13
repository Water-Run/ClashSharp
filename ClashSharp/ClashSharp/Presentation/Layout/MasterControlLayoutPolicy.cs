namespace ClashSharp.Presentation.Layout;

/// <summary>Resolved column counts for the compact status summary and mode selector.</summary>
/// <param name="ModeColumns">Number of mode controls displayed in one row.</param>
/// <param name="StatusColumns">Number of summary values displayed in one row.</param>
internal readonly record struct MasterControlLayout(
    int ModeColumns,
    int StatusColumns);

/// <summary>Calculates the master-control layout without depending on WinUI controls.</summary>
/// <remarks>
/// Invariants: Invalid widths resolve to one column; each region uses at most four columns.
/// Thread safety: Stateless and thread-safe.
/// Side effects: None.
/// </remarks>
internal static class MasterControlLayoutPolicy
{
    /// <summary>Resolves a compact layout for the available content width.</summary>
    /// <param name="contentWidth">Usable width after scroll-view padding and scrollbar allowance.</param>
    /// <returns>A deterministic layout value.</returns>
    public static MasterControlLayout Resolve(double contentWidth)
    {
        if (!double.IsFinite(contentWidth) || contentWidth <= 0)
        {
            return new MasterControlLayout(ModeColumns: 1, StatusColumns: 1);
        }

        return new MasterControlLayout(
            ModeColumns: contentWidth >= 840 ? 4 : contentWidth >= 520 ? 2 : 1,
            StatusColumns: contentWidth >= 620 ? 4 : contentWidth >= 300 ? 2 : 1);
    }
}
