using ClashSharp.Infrastructure.Networking;

namespace ClashSharp.Tests.Unit.Infrastructure;

/// <summary>Verifies HTTP status probe construction contracts without issuing network requests.</summary>
public sealed class HttpStatusProbeTests
{
    /// <summary>Verifies a zero timeout is rejected before a request can be scheduled.</summary>
    [Fact]
    public void Constructor_ZeroTimeout_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpStatusProbe(TimeSpan.Zero));
    }

    /// <summary>Verifies a negative timeout is rejected before a request can be scheduled.</summary>
    [Fact]
    public void Constructor_NegativeTimeout_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new HttpStatusProbe(TimeSpan.FromSeconds(-1)));
    }
}
