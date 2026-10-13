using ClashSharp.Presentation.Layout;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Verifies the master-control responsive layout contract.</summary>
public sealed class MasterControlLayoutPolicyTests
{
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
