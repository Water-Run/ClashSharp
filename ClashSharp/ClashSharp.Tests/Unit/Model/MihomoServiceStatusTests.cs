using ClashSharp.Model;
using ClashSharp.ServiceProtocol;

namespace ClashSharp.Tests.Unit.Model;

/// <summary>Tests the authenticated service-child ownership boundary.</summary>
public sealed class MihomoServiceStatusTests
{
    [Fact]
    public void HasReleasedChildOwnership_AcceptsStoppedScmOrAuthenticatedIdleHost()
    {
        Assert.True(new MihomoServiceStatus(true, false, "stopped").HasReleasedChildOwnership);
        Assert.True(CreateAuthenticatedIdleHost().HasReleasedChildOwnership);
        Assert.False(MihomoServiceStatus.Unknown("unknown").HasReleasedChildOwnership);
    }

    [Theory]
    [InlineData("protocol")]
    [InlineData("session")]
    [InlineData("state")]
    [InlineData("pid")]
    [InlineData("generation")]
    [InlineData("hash")]
    public void HasReleasedChildOwnership_RejectsIncompleteOrContradictoryIdleHostProof(
        string mismatch)
    {
        MihomoServiceStatus baseline = CreateAuthenticatedIdleHost();
        MihomoServiceStatus status = mismatch switch
        {
            "protocol" => baseline with { ProtocolVersion = null },
            "session" => baseline with { ServiceSessionId = Guid.Empty },
            "state" => baseline with { ChildState = MihomoServiceChildState.Faulted },
            "pid" => baseline with { ChildProcessId = 1234 },
            "generation" => baseline with { ActiveGeneration = 7 },
            "hash" => baseline with { ActiveConfigurationHash = new string('a', 64) },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch)),
        };

        Assert.False(status.HasReleasedChildOwnership);
    }

    private static MihomoServiceStatus CreateAuthenticatedIdleHost()
    {
        return new MihomoServiceStatus(true, false, "idle")
        {
            IsScmRunning = true,
            ProtocolVersion = MihomoServiceIpcProtocol.CurrentVersion,
            ServiceSessionId = Guid.NewGuid(),
            ServiceVersion = "test",
            ChildState = MihomoServiceChildState.Stopped,
        };
    }
}
