using ClashSharp.Hosting.Startup;
using ClashSharp.Model;

namespace ClashSharp.Tests.Unit.Startup;

public sealed class StartupConflictSnapshotTests
{
    [Fact]
    public void ActiveTunInterface_IsAdvisoryWhenTunIsNotRequested()
    {
        StartupConflictSnapshot snapshot = new();
        snapshot.Capture([CreateIssue(StartupConflictKind.ActiveTunInterface)]);

        Assert.False(snapshot.HasBlockingConflicts(tunRequested: false));
        Assert.True(snapshot.HasBlockingConflicts(tunRequested: true));
    }

    [Fact]
    public void NonTunConflict_RemainsBlockingWithoutTun()
    {
        StartupConflictSnapshot snapshot = new();
        snapshot.Capture([CreateIssue(StartupConflictKind.MixedPortOccupied)]);

        Assert.True(snapshot.HasBlockingConflicts(tunRequested: false));
    }

    [Fact]
    public void FailedProbe_RemainsBlockingWithoutTun()
    {
        StartupConflictSnapshot snapshot = new();
        snapshot.CaptureFailure();

        Assert.True(snapshot.HasBlockingConflicts(tunRequested: false));
    }

    private static StartupConflictIssue CreateIssue(StartupConflictKind kind)
    {
        return new StartupConflictIssue(kind, "title", "description");
    }
}
