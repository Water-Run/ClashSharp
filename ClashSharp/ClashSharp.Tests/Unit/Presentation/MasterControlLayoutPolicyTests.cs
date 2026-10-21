using ClashSharp.Presentation.Layout;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Verifies the master-control responsive layout contract.</summary>
public sealed class MasterControlLayoutPolicyTests
{
    [Fact]
    public void ResolveFlyout_RightAlignmentThatWouldOverflowFallsBackToTheAvailableLeftEdge()
    {
        MasterControlFlyoutLayout layout = MasterControlLayoutPolicy.ResolveFlyout(800, 600, 100, 132, 42, 185, 245, true);
        Assert.False(layout.AlignRight);
        Assert.Equal(480, layout.ContentWidth);
        Assert.True(185 + layout.ContentWidth + 24 <= 800 - 16);
    }

    [Fact]
    public void ResolveFlyout_LeftAlignmentThatWouldOverflowUsesTheAvailableRightEdge()
    {
        MasterControlFlyoutLayout layout = MasterControlLayoutPolicy.ResolveFlyout(800, 600, 100, 132, 42, 555, 615);
        Assert.True(layout.AlignRight);
        Assert.True(615 - layout.ContentWidth - 24 >= 16);
    }

    [Theory]
    [InlineData(100, 132, false)]
    [InlineData(490, 522, true)]
    [InlineData(48, 80, false)]
    public void ResolveFlyout_FitsOnTheAvailableSideWithoutEnteringTheTitleBar(double top, double bottom, bool above)
    {
        MasterControlFlyoutLayout layout = MasterControlLayoutPolicy.ResolveFlyout(800, 600, top, bottom, 42);
        Assert.Equal(above, layout.OpensAbove);
        double available = above ? top - 60 : 600 - bottom - 16;
        Assert.InRange(layout.ListMaxHeight + 42 + 24, 1, available);
    }

    [Theory]
    [InlineData(800, 480)]
    [InlineData(400, 336)]
    [InlineData(200, 136)]
    public void ResolveFlyout_ConstrainContentWidthToTheWindow(double width, double expected)
    {
        Assert.Equal(expected, MasterControlLayoutPolicy.ResolveFlyout(width, 600, 100, 132, 42).ContentWidth);
    }

    [Fact]
    public void ResolveFlyout_InvalidMeasurementsRemainFiniteAndPositive()
    {
        MasterControlFlyoutLayout layout = MasterControlLayoutPolicy.ResolveFlyout(double.NaN, double.PositiveInfinity, double.NaN, double.NaN, double.NaN);
        Assert.InRange(layout.ContentWidth, 1, 480);
        Assert.InRange(layout.ListMaxHeight, 1, 420);
    }

    [Fact]
    public void ResolveFlyout_MeasuredLongFooterReducesTheScrollingFields()
    {
        MasterControlFlyoutLayout shortFooter = MasterControlLayoutPolicy.ResolveFlyout(800, 600, 160, 192, 42);
        MasterControlFlyoutLayout longFooter = MasterControlLayoutPolicy.ResolveFlyout(800, 600, 160, 192, 104);
        Assert.Equal(shortFooter.ListMaxHeight - 62, longFooter.ListMaxHeight);
        Assert.Equal(shortFooter.OpensAbove, longFooter.OpensAbove);
    }

    /// <summary>The measured content width of the minimum compact-navigation window remains side by side.</summary>
    [Fact]
    public void Resolve_MinimumWindowContent_UsesSideBySideLayout()
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(668);

        Assert.True(layout.IsSideBySide);
        Assert.InRange(layout.ModeColumnWidth, 280, 360);
    }

    /// <summary>The exact responsive breakpoint enters the horizontal layout.</summary>
    [Fact]
    public void Resolve_AtBreakpoint_UsesSideBySideLayout()
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(620);

        Assert.True(layout.IsSideBySide);
        Assert.Equal(280, layout.ModeColumnWidth);
    }

    /// <summary>Narrow content stacks controls instead of squeezing either region.</summary>
    [Theory]
    [InlineData(619.9)]
    [InlineData(320)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Resolve_NarrowOrInvalidContent_UsesStackedLayout(double contentWidth)
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(contentWidth);

        Assert.False(layout.IsSideBySide);
        Assert.Equal(0, layout.ModeColumnWidth);
    }

    /// <summary>Wide content caps the mode region so the hero can continue expanding.</summary>
    [Fact]
    public void Resolve_WideContent_CapsModeColumnWidth()
    {
        MasterControlLayout layout = MasterControlLayoutPolicy.Resolve(1400);

        Assert.True(layout.IsSideBySide);
        Assert.Equal(360, layout.ModeColumnWidth);
    }
}
