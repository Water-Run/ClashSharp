using ClashSharp.Presentation.Layout;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class MasterTileMotionPolicyTests
{
    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(7.9, 0, false)]
    [InlineData(0, 7.9, false)]
    [InlineData(8, 0, true)]
    [InlineData(0, -8, true)]
    [InlineData(6, 6, true)]
    [InlineData(double.NaN, 0, false)]
    public void MouseHold_BecomesDragOnlyAfterSpatialThreshold(double x, double y, bool dragging)
    {
        Assert.Equal(dragging, MasterTileMotionPolicy.ShouldStartMouseDrag(x, y));
    }
    [Fact]
    public void CenterPress_DepressesWithoutTilting()
    {
        var center = MasterTileMotionPolicy.GetPressPose(140, 58, 280, 116);
        Assert.Equal(0, center.RotationX);
        Assert.Equal(0, center.RotationY);
        Assert.True(center.Depth < 0);
    }

    [Theory]
    [InlineData(0, 58, 0, 17)]
    [InlineData(280, 58, 0, -17)]
    [InlineData(140, 0, -17, 0)]
    [InlineData(140, 116, 17, 0)]
    public void PressedEdge_RotatesIntoTheWinUiProjectionPlane(double x, double y, double rotationX, double rotationY)
    {
        var pose = MasterTileMotionPolicy.GetPressPose(x, y, 280, 116);
        Assert.Equal(rotationX, pose.RotationX);
        Assert.Equal(rotationY, pose.RotationY);
    }

    [Theory]
    [InlineData(0, 58, 280, 58)]
    [InlineData(140, 0, 140, 116)]
    [InlineData(0, 0, 280, 116)]
    public void OppositePresses_TiltInOppositeDirections(double x, double y, double oppositeX, double oppositeY)
    {
        var first = MasterTileMotionPolicy.GetPressPose(x, y, 280, 116);
        var opposite = MasterTileMotionPolicy.GetPressPose(oppositeX, oppositeY, 280, 116);
        Assert.Equal(-first.RotationX, opposite.RotationX);
        Assert.Equal(-first.RotationY, opposite.RotationY);
        Assert.Equal(first.Depth, opposite.Depth);
        Assert.Equal(0, first.Depth);
    }

    [Fact]
    public void RelativePosition_IsStableAcrossDpiAndAspectRatio()
    {
        var original = MasterTileMotionPolicy.GetPressPose(70, 87, 280, 116);
        var scaled = MasterTileMotionPolicy.GetPressPose(140, 174, 560, 232);
        var wide = MasterTileMotionPolicy.GetPressPose(140, 87, 560, 116);
        Assert.Equal(original.RotationX, scaled.RotationX);
        Assert.Equal(original.RotationY, scaled.RotationY);
        Assert.Equal(original, wide);
    }

    [Fact]
    public void CapturedPointerOutsideSurface_IsClampedToAnEdge()
    {
        var edge = MasterTileMotionPolicy.GetPressPose(0, 116, 280, 116);
        var outside = MasterTileMotionPolicy.GetPressPose(-200, 500, 280, 116);
        Assert.Equal(edge, outside);
    }

    [Theory]
    [InlineData(0, 0, 0, 116)]
    [InlineData(0, 0, 280, -1)]
    [InlineData(double.NaN, 0, 280, 116)]
    [InlineData(0, double.PositiveInfinity, 280, 116)]
    [InlineData(0, 0, double.PositiveInfinity, 116)]
    public void InvalidOrUnmeasuredSurface_HasNoTransform(double x, double y, double width, double height)
    {
        Assert.Equal(default, MasterTileMotionPolicy.GetPressPose(x, y, width, height));
    }
}
