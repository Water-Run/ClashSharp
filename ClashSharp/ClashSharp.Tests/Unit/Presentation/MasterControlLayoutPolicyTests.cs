using ClashSharp.Presentation.Layout;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Verifies the master-control responsive layout contract.</summary>
public sealed class MasterControlLayoutPolicyTests
{
    /// <summary>The minimum compact-navigation window presents four summary values and two modes per row.</summary>
    [Fact]
    public void Resolve_MinimumWindowContent_UsesCompactRows()
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(668);

        Assert.Equal(2, layout.ModeColumns);
        Assert.Equal(4, layout.StatusColumns);
    }

    /// <summary>Summary values and descriptive mode controls reflow at independent width boundaries.</summary>
    [Theory]
    [InlineData(299.9, 1, 1)]
    [InlineData(300, 1, 2)]
    [InlineData(519.9, 1, 2)]
    [InlineData(520, 2, 2)]
    [InlineData(619.9, 2, 2)]
    [InlineData(620, 2, 4)]
    [InlineData(839.9, 2, 4)]
    [InlineData(840, 4, 4)]
    public void Resolve_WidthBoundaries_KeepEachRegionReadable(double width, int modeColumns, int statusColumns)
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(width);

        Assert.Equal(modeColumns, layout.ModeColumns);
        Assert.Equal(statusColumns, layout.StatusColumns);
    }

    /// <summary>Invalid measurements use one safe column without propagating non-finite dimensions.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Resolve_InvalidContent_UsesOneColumn(double contentWidth)
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(contentWidth);

        Assert.Equal(1, layout.ModeColumns);
        Assert.Equal(1, layout.StatusColumns);
    }

    /// <summary>Wide content uses at most four columns rather than spreading values indefinitely.</summary>
    [Fact]
    public void Resolve_WideContent_CapsBothRegionsAtFourColumns()
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(1400);

        Assert.Equal(4, layout.ModeColumns);
        Assert.Equal(4, layout.StatusColumns);
    }
}
