using ClashSharp.Presentation.Dialogs;

namespace ClashSharp.Tests.Unit.Presentation;

public sealed class DialogWindowBoundsPolicyTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PreferredDialogCanExceedItsSmallOwner(double scale)
    {
        var work = new DialogWindowBoundsPolicy.Bounds(0, 0, 3840, 2160);
        var small = new DialogWindowBoundsPolicy.Bounds(1200, 700, 320, 240);
        var large = new DialogWindowBoundsPolicy.Bounds(200, 100, 2200, 1600);
        var fromSmall = DialogWindowBoundsPolicy.Place(work, small, scale, 688, 600);
        var fromLarge = DialogWindowBoundsPolicy.Place(work, large, scale, 688, 600);

        Assert.True(fromSmall.Width > small.Width);
        Assert.True(fromSmall.Height > small.Height);
        Assert.Equal(fromLarge.Width, fromSmall.Width);
        Assert.Equal(fromLarge.Height, fromSmall.Height);
        AssertContained(work, fromSmall);
        AssertContained(work, fromLarge);
    }

    [Fact]
    public void PlacementRemainsInsideWorkAreasAcrossDpiAndOffScreenOwners()
    {
        Random random = new(0x416);
        for (int index = 0; index < 2000; index++)
        {
            var work = new DialogWindowBoundsPolicy.Bounds(random.Next(-10000, 10000), random.Next(-10000, 10000),
                random.Next(1, 5000), random.Next(1, 3000));
            var owner = new DialogWindowBoundsPolicy.Bounds(random.Next(-20000, 20000), random.Next(-20000, 20000),
                random.Next(1, 3000), random.Next(1, 2000));
            var actual = DialogWindowBoundsPolicy.Place(work, owner, random.Next(48, 385) / 96d,
                random.Next(1, 2000), random.Next(1, 3000));
            AssertContained(work, actual);
        }
    }

    [Fact]
    public void OwnerCenteredDialogKeepsItsPreferredSizeWhenThereIsRoom()
    {
        var actual = DialogWindowBoundsPolicy.Place(new(0, 0, 1920, 1080), new(500, 300, 800, 600), 1, 688, 500);
        Assert.Equal(new DialogWindowBoundsPolicy.Bounds(556, 350, 688, 500), actual);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void InvalidScaleCannotProduceNativeCoordinates(double scale)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DialogWindowBoundsPolicy.Place(new(0, 0, 1920, 1080), new(0, 0, 800, 600), scale, 688, 500));
    }

    private static void AssertContained(DialogWindowBoundsPolicy.Bounds work, DialogWindowBoundsPolicy.Bounds actual)
    {
        Assert.InRange(actual.Width, 1, work.Width);
        Assert.InRange(actual.Height, 1, work.Height);
        Assert.True(actual.X >= work.X && actual.Y >= work.Y);
        Assert.True((long)actual.X + actual.Width <= (long)work.X + work.Width);
        Assert.True((long)actual.Y + actual.Height <= (long)work.Y + work.Height);
    }
}
